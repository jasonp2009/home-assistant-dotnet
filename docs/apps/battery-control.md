# Battery control app

Price-arbitrage battery controller. It charges the home battery when grid power is cheap and
discharges it when power is expensive, using [Amber Electric](https://amber.com.au) wholesale-linked
prices and a [Forecast.Solar](https://forecast.solar) generation forecast, while respecting the
battery's capacity limits and Amber demand windows.

- Code: [`../../src/apps/HassModel/Battery/`](../../src/apps/HassModel/Battery/)
- Config: [`../../src/apps/HassModel/Battery/BatteryControl.yaml`](../../src/apps/HassModel/Battery/BatteryControl.yaml)
- Tests: [`../../test/apps/HassModel/Battery/`](../../test/apps/HassModel/Battery/)

## How it runs

[`BatteryControl`](../../src/apps/HassModel/Battery/BatteryControl.cs) is a `[NetDaemonApp]`. Its
constructor registers a scheduler that fires `CheckAndUpdateBatteryModeAsync` every
`SegmentSizeMins` (5 min), aligned to segment boundaries. Each run decides a single action for the
**current** 5-minute segment — `Buy`, `Sell`, or `None` — and sets the inverter work mode
accordingly.

## Decision flow

`GetCurrentActionAsync` → `InitialiseEnergySegmentsAsync`:

1. **Build the forecast horizon.** Create a list of [`EnergySegment`](../../src/apps/HassModel/Battery/Models/EnergySegment.cs)s,
   one per 5-minute slot, spanning at least `MinForecastHours` (72 h). Each segment is seeded with a
   projected battery charge: start from the current state of charge, then for each subsequent
   segment subtract the estimated usage (a **per-time-of-day** estimate — see
   [Segmented usage estimate](#segmented-usage-estimate)) and add the solar forecast.
   - Amber prices ([`AmberClient.GetCurrentPriceAsync`](../../src/apps/HassModel/Battery/Clients/AmberClient/AmberClient.cs))
     and the solar forecast ([`ForecastSolarClient`](../../src/apps/HassModel/Battery/Clients/ForecastSolarClient/ForecastSolarClient.cs))
     are fetched in parallel.
   - The loop waits up to `MaxPriceLockInWaitSecs` for the current interval's price to "lock in"
     (i.e. stop being an `estimate`) before committing.
   - [`ApplyPrice`](../../src/apps/HassModel/Battery/Extensions/EnergySegmentExtensions.cs) assigns
     each segment its buy price (Amber `General`/`ControlledLoad` channels) and sell price (`FeedIn`
     channel, **stored negated**), picking the Amber interval with the greatest time overlap.
     `ApplySolarForecast` adds expected solar generation. The forecast request also passes today's
     measured production (`SolarProductionTodayEntity`) via Forecast.Solar's
     [`actual`](https://doc.forecast.solar/actual) parameter, which recalibrates the **current day's**
     forecast to real output (later days are unaffected); a missing/non-numeric sensor simply omits it.

2. **Greedy boundary solver.** While the projected charge crosses a capacity limit
   (`CalculateBoundaryResult`):
   - **Above `MaxCapacity`** → pick the segment with the **highest** weighted price in the window
     and mark it `Sell`.
   - **Below `MinCapacity`** → pick the segment with the **lowest** weighted price (excluding demand
     windows) and mark it `Buy`.
   - Apply the action, re-simulate charge forward for all later segments, and repeat until the
     projection stays within `[MinCapacity, MaxCapacity]`. The inverter's per-segment rate caps the
     battery's net change for the segment it acts on, so the applied move is
     `rate − NaturalChargeDeltaKwh`: the action **subsumes** that segment's own solar/usage flow (already
     baked into the projection) rather than stacking the full rate on top of it. A forced charge on a
     sunny segment rises by the rate (solar surplus and load met through the grid within the cap); a
     forced discharge on a high-usage segment falls by the rate (the load served from the discharge).

3. **Opportunistic price arbitrage** ([`ApplyArbitrage`](../../src/apps/HassModel/Battery/BatteryPlanner.cs)).
   After the boundary solver, greedily pair an un-actioned **buy** segment (cheap) with an un-actioned
   **sell** segment (dear) and commit the pair when its net clears the gate:
   `sellEarning − buyCost / RoundTripEfficiency ≥ ArbitrageMinMarginPerKwh`. Each pair moves one segment's
   worth of charge; a feasibility check keeps the held round-trip inside `[MinCapacity, MaxCapacity]`.
   Forecast (estimate) legs are **pessimised** — a buy leaned toward its advanced `High`, a sell toward its
   lowest plausible earning — so the planner won't chase a higher-but-uncertain forecast over a certain
   price; locked (materialised) legs pass through at face value. The pessimism is **directional**:
   buy-before-sell pairs (charge now, export later) use the lower `ArbitrageBuyBeforeSellWeight` because
   charging is low-regret — energy that never gets sold still serves household load — while sell-before-buy
   pairs keep the full `ArbitragePessimismWeight`. No-op when `ArbitrageEnabled` is false.
   - **Sell-before-buy legs are priced no more cheaply than the boundary solver would price them.** Each leg
     of a sell-before-buy pair uses `max(ArbitragePessimismWeight, GetRiskWeight(runway))` — the *more*
     pessimistic of arbitrage's flat weight and the runway weight `OptimiseSegments` applies to the same
     segment. Arbitrage is discretionary where the solver is mandatory, so it must never be the more
     optimistic of the two: otherwise it sells now against a cheap refill the solver has already decided it
     will not wait for. `max` (not substitution) matters because `GetRiskWeight` goes **negative** at deep
     runway, and deferring to it unconditionally would price a full-battery refill *below* predicted.
   - **The feasibility floor for a sell-before-buy pair scales with the drain it holds through** —
     `ArbitrageHoldDrainReserveFraction` (see below). The fixed one-step buffer is a structural guard that
     does not grow with hold length, so a pair holding through 15 kWh of projected drain was judged on the
     same 0.83 kWh margin as one holding through 0.3 kWh.

4. **Reversal cooldown.** Before acting, `BatteryControl.ApplyReversalCooldown` suppresses the current
   segment's action to `None` if it is the **opposite** of the last action actually sent to the inverter and
   fewer than `ActionReversalCooldownSegments` segments have passed
   ([`BatteryPlanner.ApplyActionReversalCooldown`](../../src/apps/HassModel/Battery/BatteryPlanner.cs)).
   The planner re-derives the whole plan every 5 minutes and holds no memory, so oscillation between
   consecutive runs is invisible from inside a single plan — hence an actuation-level rule, with the "what
   did I do last" state on `BatteryControl` so `BatteryPlanner` stays pure. Two safety asymmetries: it only
   ever downgrades to `None` (it can never force a discharge to continue), and it **never blocks a Usage
   (floor-defence) buy**, since refusing to charge can strand the battery on the floor. Blocking a
   max-boundary *sell* is safe because the inverter exports surplus at 100% SoC anyway (see below).

5. **Act + log.** Set the inverter `BatteryModeSelectEntity` to the charge/discharge/none mode for
   the current segment, and write the next action, its price, its time, and the projected
   "battery until empty" time to Home Assistant helper entities.

## Weighted price (the ranking key)

[`GetWeightedPrice(segment, isBuy, config, hourlyUsageKwh)`](../../src/apps/HassModel/Battery/Extensions/EnergySegmentExtensions.cs)
is what the greedy solver ranks segments by (`MinBy` for buys, `MaxBy` for sells). It adjusts an
Amber price by a **risk weight** derived from how much battery runway is left:

1. **Runway, not %.** `GetHoursToEmpty = (projectedCharge − MinCapacity) / hourlyUsage`. The hourly
   usage is the net 3-day average (see below). Usage is floored so net-production segments don't
   divide by zero.
2. **Signed risk weight** (`GetRiskWeight`): two independent ramps on the hours-to-empty axis.
   - **Short runway → pessimism** (positive weight, up to `PessimismMaxWeight`): lean an estimated
     price toward Amber's `High` bound for buys (or `Low` for sells) — i.e. treat uncertain future
     prices as worse, so the controller prefers to act at known-good prices now.
   - **Deep runway → optimism** (negative weight, up to `OptimismMaxWeight`): lean the other way.
   - **In between → neutral** (0): use Amber's `predicted` price as-is.
   - The defaults make pessimism **much stronger and wider** than optimism — it maxes at `2`
     (extrapolating past the bound, see below), but only right at the floor.
3. **Application:**
   - Locked-in (non-estimate) prices are returned **raw**, ignoring the weight.
   - With an advanced price, blend: `predicted·(1−|w|) + (High|Low)·|w|`. With a weight `|w| > 1` the
     blend **extrapolates past the bound** (e.g. `w = 2` → `2·High − predicted`), pricing a near-floor
     buy *above* the advanced `High` so floor-defense buys prefer to charge early rather than wait.
   - Without an advanced price (Amber only provides one for ~the first 24 h; beyond that just
     `perKwh`), scale `perKwh` by **pessimism only** — optimism is clamped off, so the controller
     never optimistically waits for an un-forecast far-future price.

The usage rate is passed in (not read inside the function) specifically so a future **time-of-day**
usage estimate can be supplied per segment without changing the weighting logic.

See the unit tests in [`../../test/apps/HassModel/Battery/Extensions/`](../../test/apps/HassModel/Battery/Extensions/)
for worked examples of every branch.

## Segmented usage estimate

The per-segment **drain** in `BuildSegments` is a learned, time-of-day consumption estimate (so a
3 a.m. segment and a 6 p.m. segment deplete by realistic, different amounts), produced by
[`UsageTracker`](../../src/apps/HassModel/Battery/Usage/UsageTracker.cs) + the pure
[`UsageMath`](../../src/apps/HassModel/Battery/Usage/UsageMath.cs):

1. **Measure consumption** from the cumulative counters as a delta between readings:
   `ΔgridIn − ΔgridOut + Δsolar − (Δcharge − Δdischarge)`. The battery charge/discharge counters reset
   daily; the reset is **rebased across** (`UsageMath.RebaseResets` / the live equivalent in
   `UsageTracker`) — the pre-reset total is carried forward so the window straddling midnight still
   yields a sample, instead of being dropped and leaving the midnight buckets to the flat fallback.
2. **Solar-aligned windowing.** The solar lifetime counter only advances every ~15 min, so
   consumption is measured over a window that closes when solar ticks (or at `UsageMaxWindowSegments`
   for night/no-generation) and **spread evenly** across the 5-minute segments it covers — the same
   anti-sawtooth trick as `ApplySolarForecast`.
3. **Storage is in-memory; HA is the source of truth.** On startup the sample set is backfilled from
   `UsageBackfillDays` of HA history (via [`HaHistoryClient`](../../src/apps/HassModel/Battery/Clients/HaHistoryClient/HaHistoryClient.cs))
   and then extended live each run. A restart simply re-pulls from HA, so gaps self-heal.
4. **Estimate** for a target segment: average samples sharing its local time-of-day over the last
   1 / 3 / 7 days, blended `0.4 / 0.3 / 0.3` (renormalised over windows that have data), times
   `EstimatedUsageMultiplier`. Any time-of-day bucket with no data falls back to the flat 3-day
   average, so the estimate is never 0.
5. **Demand-window uplift.** For segments Amber flags as a demand window, `BuildSegments` swaps
   `EstimatedUsageMultiplier` for the higher `DemandWindowUsageMultiplier` (1.5) — inflating the
   projected drain there so the plan reserves more charge and doesn't get forced into an expensive
   grid import mid-window if load spikes. Left unset (0) demand windows drain like any other segment.

The **runway** risk weight (above) still uses the flat 3-day *average* hourly usage, not the
time-of-day estimate: runway is an average-rate concept, and an instantaneous near-zero overnight rate
would make hours-to-empty effectively infinite.

## Key files

| File | Role |
|---|---|
| [`BatteryControl.cs`](../../src/apps/HassModel/Battery/BatteryControl.cs) | Scheduler + Home Assistant I/O (reads state, sets inverter mode, logs); delegates planning to `BatteryPlanner` |
| [`BatteryPlanner.cs`](../../src/apps/HassModel/Battery/BatteryPlanner.cs) | Pure, unit-testable planning: `BuildSegments`, `OptimiseSegments` (greedy boundary solver), `ApplyArbitrage` (opportunistic buy-low/sell-high), `CalculateBoundaryResult`, `GetBatteryUntil` |
| [`BatteryConfig.cs`](../../src/apps/HassModel/Battery/BatteryConfig.cs) | Typed config bound from the YAML |
| [`BatteryControl.yaml`](../../src/apps/HassModel/Battery/BatteryControl.yaml) | Entity ids + tuning values |
| [`Models/EnergySegment.cs`](../../src/apps/HassModel/Battery/Models/EnergySegment.cs) | A 5-minute slot: projected charge, prices, solar, action |
| [`Extensions/EnergySegmentExtensions.cs`](../../src/apps/HassModel/Battery/Extensions/EnergySegmentExtensions.cs) | `ApplyPrice`, `ApplySolarForecast`, `GetHoursToEmpty`, `GetRiskWeight`, `GetWeightedPrice` |
| [`Usage/UsageMath.cs`](../../src/apps/HassModel/Battery/Usage/UsageMath.cs) | Pure usage maths: `ComputeConsumption`, `SpreadWindow`, `BuildSamplesFromReadings`, `EstimateSegmentUsage` |
| [`Usage/UsageTracker.cs`](../../src/apps/HassModel/Battery/Usage/UsageTracker.cs) | In-memory sample store: startup backfill + per-run live update + `BuildEstimator` |
| [`Clients/AmberClient/`](../../src/apps/HassModel/Battery/Clients/AmberClient/) | Amber API client, interval models (`BaseInterval`/`Current`/`Forecast`/`Actual`), `AdvancedPrice`, channel/descriptor enums |
| [`Clients/ForecastSolarClient/`](../../src/apps/HassModel/Battery/Clients/ForecastSolarClient/) | Forecast.Solar API client |
| [`Clients/HaHistoryClient/`](../../src/apps/HassModel/Battery/Clients/HaHistoryClient/) | Minimal HA REST history client used to backfill the usage estimate on startup |

## Behaviour notes & known trade-offs

- The decision logic is unit-tested — see `test/apps/HassModel/Battery/` and the scenario findings + owner
  triage in [`../battery-scenario-findings.md`](../battery-scenario-findings.md).
- **Inverter:** it exports surplus to the grid at 100% SoC (it does **not** curtail), so relieving an
  over-charge by discharging at the best available feed-in is the correct, loss-minimising action.
- **No charging during demand windows** is intentional (the home draws from the grid; demand windows carry
  excess usage charges). Selling in a demand window is allowed; only buying is disallowed. Because buying is
  disallowed there, the plan instead **reserves more charge going into** a demand window via
  `DemandWindowUsageMultiplier` (the projected drain across those segments is inflated), reducing the chance
  of being forced to import mid-window.
- **Price arbitrage** (buy import low / export high *without* a capacity-boundary trigger) runs after the
  boundary solver — see step 3 of the [decision flow](#decision-flow) and the **Price arbitrage** config
  table below. The original design notes are in [`../battery-arbitrage-plan.md`](../battery-arbitrage-plan.md).

## Configuration reference (`BatteryControl.yaml`)

**Battery & rates**

| Key | Default | Meaning |
|---|---|---|
| `BatteryCapacity` | 53 | Total battery capacity (kWh) |
| `MinCapacity` | 8 | Lower bound; charge below this triggers buying (kWh) |
| `MaxCapacity` | 50 | Upper bound; charge above this triggers selling (kWh) |
| `ChargeRateKw` / `DischargeRateKw` | 12 | Charge/discharge power (kW); per-segment kWh is derived |
| `SegmentSizeMins` | 5 | Decision interval / price resolution |
| `MinForecastHours` | 72 | Minimum planning horizon |
| `EstimatedUsageMultiplier` | 1.1 | Safety margin on the measured usage estimate |
| `DemandWindowUsageMultiplier` | 1.5 | Usage multiplier used **instead of** `EstimatedUsageMultiplier` for demand-window segments, so more charge is reserved through the window; 0 disables it |
| `MaxPriceLockInWaitSecs` / `MaxPriceLockInRetryDelaySecs` | 30 / 2 | How long to wait for the current price to lock in |

**Risk weighting (hours-to-empty ramps)** — pessimism intentionally wider/stronger than optimism

| Key | Default | Meaning |
|---|---|---|
| `PessimismStartHours` | 12 | Runway (h) below which pessimism begins ramping |
| `PessimismMaxAtHours` | 0 | Runway (h) at/below which pessimism is maxed (`0` = full weight only right at the floor) |
| `PessimismMaxWeight` | 2 | Max pessimism blend fraction. **A value >1 extrapolates *past* the bound** (`w=2` → `2·High − predicted`), so near-floor buy estimates are priced **above** the advanced `High`. This is deliberate: it makes floor-defense buys **prefer charging early** — a later, lower-SoC segment must beat the locked "now" price even after this markup before the solver will defer to it |
| `OptimismStartHours` | 20 | Runway (h) above which optimism begins ramping |
| `OptimismMaxAtHours` | 24 | Runway (h) at/above which optimism is maxed |
| `OptimismMaxWeight` | 0.3 | Max optimism blend fraction |

The ramp is linear: weight `= MaxWeight · (StartHours − runway) / (StartHours − MaxAtHours)`, clamped. With the
defaults that is `0` at ≥12 h runway → `1.0` at 6 h → `1.5` at 3 h → `2.0` at the floor. Because the full
`2` is reached only at 0 h runway, the *effective* weight at the runways where buys are actually placed
(≈2–6 h) is ~`1.0`–`1.5`.

**Price arbitrage** — opportunistic buy-low/sell-high layered on top of the boundary solver

| Key | Default | Meaning |
|---|---|---|
| `ArbitrageEnabled` | true | Master switch for the arbitrage pass |
| `ArbitragePessimismWeight` | 0.5 | How far a forecast leg leans to its worst plausible price (buy `High` / sell lowest-earning); used for sell-before-buy pairs |
| `ArbitrageBuyBeforeSellWeight` | 0.25 | Lower pessimism for buy-before-sell (charge-first) pairs — charging is low-regret, so they are judged less conservatively. Set ≤ `ArbitragePessimismWeight`, and not 0 |
| `RoundTripEfficiency` | 0.9 | Charge→discharge efficiency; divides the buy cost in the profit gate only |
| `ArbitrageMinMarginPerKwh` | 2 | Minimum net profit (c/kWh) required to commit a pair |
| `ArbitrageHoldDrainReserveFraction` | 1.0 | Extra charge a **sell-before-buy** pair must keep above the floor, as a fraction of the household drain projected between the sell and the refill, on top of the fixed one-step buffer. The value **is** the tolerance: `k` means "the pair must still clear the floor if the drain runs `(1+k)×` the estimate". Set to 1.0 because the measured drain on 2026-08-12 ran ~2× the time-of-day estimate through the morning. `0` disables it. **Min side only** — a drain under-estimate makes *over*-filling less likely, not more. Known gap: haircuts drain only, not forecast solar |
| `ActionReversalCooldownSegments` | 3 | Segments during which an action blocks the **opposite** action (0 = disabled). Only ever downgrades to `None`, and never blocks a floor-defence (`Usage`) buy |

**Segmented usage estimate**

| Key | Default | Meaning |
|---|---|---|
| `UsageBackfillDays` | 7 | Days of HA history pulled on startup to seed the estimate |
| `UsageMaxWindowSegments` | 4 | Measurement-window cap (segments); closes night/no-solar windows and bounds gaps |
| `UsageMaxSegmentKwh` | 5 | Per-segment sanity cap; windows above it are discarded |
| `UsageWindow1Days` / `UsageWindow1Weight` | 1 / 0.4 | Recent window (days back) and blend weight |
| `UsageWindow2Days` / `UsageWindow2Weight` | 3 / 0.3 | Mid window |
| `UsageWindow3Days` / `UsageWindow3Weight` | 7 / 0.3 | Long window |

**Inverter modes** — strings matching the work-mode `select` options:
`BatteryNoneMode` = `Self-consumption mode`, `BatteryChargeMode` = `Reserve power mode`,
`BatteryDischargeMode` = `Custom mode`.

## Home Assistant entities

**Read (state inputs)**

| Config key | Entity |
|---|---|
| `SolarBatteryStateOfChargeEntity` | `sensor.ai_hb_g2_series_battery_state_of_charge` (SoC %) |
| `GridIn3DaysEntity` | `sensor.energy_meter_grid_in_3_days` |
| `GridOut3DaysEntity` | `sensor.energy_meter_grid_out_3_days` |
| `SolarProduction3DaysEntity` | `sensor.pawar_plant_total_solar_production_3_days` |
| `SolarProductionTodayEntity` | `sensor.pawar_plant_total_energy_today` (today's kWh, resets at midnight; Forecast.Solar `actual` calibration) |
| `BatteryChargeDiff3DaysEntity` | `sensor.solar_battery_battery_diff_3_days` |
| `GridEnergyInTotalEntity` | `sensor.energy_meter_grid_energy_in_total` (lifetime kWh) |
| `GridEnergyOutTotalEntity` | `sensor.energy_meter_grid_energy_out_total` (lifetime kWh) |
| `SolarLifetimeOutputEntity` | `sensor.pawar_plant_total_lifetime_energy_output` (lifetime kWh, ~15-min updates) |
| `BatteryEnergyChargingEntity` | `sensor.ai_hb_g2_series_battery_energy_for_charging` (kWh, daily reset) |
| `BatteryEnergyDischargingEntity` | `sensor.ai_hb_g2_series_battery_energy_for_discharging` (kWh, daily reset) |

The four 3-day sensors give the **flat average** hourly usage (`gridIn − gridOut + solar − batteryUsage`,
averaged per segment, scaled by `EstimatedUsageMultiplier`). That average drives the runway risk weight
and is the per-segment fallback. The per-segment **drain** itself comes from the cumulative counters via
the [segmented usage estimate](#segmented-usage-estimate).

**Write (control)**

- `BatteryModeSelectEntity` = `select.battery_2003025b090201_work_mode` — the inverter work mode.

**Write (logging helpers)** — for observability/dashboards, not used in decisions:
`input_select.current_action_battery_log`, `input_datetime.current_action_end_battery_log`,
`input_datetime.battery_until_battery_log`, `input_select.next_action_battery_log`,
`input_number.next_action_price_battery_log`, `input_datetime.next_action_at_battery_log`.

> For validating behaviour, `sensor.home_general_price` and `sensor.home_feed_in_price` (from the
> default Amber HA integration) give the actual prices paid/received and can be correlated against
> the `current_action_battery_log` history via the HA REST API.

## External APIs

- **Amber**: `GET https://api.amber.com.au/v1/sites/{siteId}/prices/current?next={n}&resolution=5`.
  Returns polymorphic intervals; `advancedPrice` (low/predicted/high) is present for ~24 h only.
  Key + site id in `AmberClientSettings` (`src/appsettings.json`).
- **Forecast.Solar**: generation forecast keyed by lat/long/array config in
  `ForecastSolarClientSettings`.

## Testing

`dotnet test` (xUnit). Coverage for this app lives under
[`../../test/apps/HassModel/Battery/`](../../test/apps/HassModel/Battery/):

- `Extensions/RiskWeightTests.cs` — the runway ramps and `GetHoursToEmpty` edge cases.
- `Extensions/GetWeightedPriceTests.cs` — locked-in passthrough, the blend, the beyond-24 h
  optimism clamp, the sell side, and the uncertainty-ordering property the solver relies on.
- `Extensions/ApplyPriceTests.cs` — channel routing, price selection, negated sell price, demand
  window, overlap selection.
- `BatteryArbitrageTests.cs` — the arbitrage profit gate, feasibility, estimate-based pessimism, locked
  vs forecast legs, demand-window rules, and the directional buy-before-sell weight.
- `BatteryArbitrageAuditTests.cs` — end-to-end harness driving the real
  `BuildSegments → OptimiseSegments → ApplyArbitrage` pipeline with the **deployed** config and a 42 h
  price/usage/solar shape taken from 2026-08-12, written as before/after comparisons against the
  pre-fix config. See [`../battery-arbitrage-fix-review.md`](../battery-arbitrage-fix-review.md).
- `BatteryArbitrageHoldReserveTests.cs` — the hold-window drain reserve, including the short-hold,
  low-drain and reserve-disabled **controls** that give the long-hold rejection its meaning.
- `BatteryReversalCooldownTests.cs` — the reversal cooldown, notably that a floor-defence buy is never
  blocked and that the guard never forces an action to continue.
- `Clients/AmberClient/Extensions/BaseIntervalExtensionsTests.cs` — interval price helpers.
