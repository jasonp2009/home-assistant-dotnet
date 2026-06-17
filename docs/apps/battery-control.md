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
     `ApplySolarForecast` adds expected solar generation.

2. **Greedy boundary solver.** While the projected charge crosses a capacity limit
   (`CalculateBoundaryResult`):
   - **Above `MaxCapacity`** → pick the segment with the **highest** weighted price in the window
     and mark it `Sell`.
   - **Below `MinCapacity`** → pick the segment with the **lowest** weighted price (excluding demand
     windows) and mark it `Buy`.
   - Apply the action, re-simulate charge forward for all later segments, and repeat until the
     projection stays within `[MinCapacity, MaxCapacity]`.

3. **Act + log.** Set the inverter `BatteryModeSelectEntity` to the charge/discharge/none mode for
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
   - The defaults make pessimism **stronger and wider** than optimism.
3. **Application:**
   - Locked-in (non-estimate) prices are returned **raw**, ignoring the weight.
   - With an advanced price, blend: `predicted·(1−|w|) + (High|Low)·|w|`.
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

The **runway** risk weight (above) still uses the flat 3-day *average* hourly usage, not the
time-of-day estimate: runway is an average-rate concept, and an instantaneous near-zero overnight rate
would make hours-to-empty effectively infinite.

## Key files

| File | Role |
|---|---|
| [`BatteryControl.cs`](../../src/apps/HassModel/Battery/BatteryControl.cs) | Scheduler + Home Assistant I/O (reads state, sets inverter mode, logs); delegates planning to `BatteryPlanner` |
| [`BatteryPlanner.cs`](../../src/apps/HassModel/Battery/BatteryPlanner.cs) | Pure, unit-testable planning: `BuildSegments`, `OptimiseSegments` (greedy boundary solver), `CalculateBoundaryResult`, `GetBatteryUntil` |
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
  excess usage charges).
- **Not yet implemented:** proactive price arbitrage (buy low / sell high without a capacity-boundary trigger)
  — planned in [`../battery-arbitrage-plan.md`](../battery-arbitrage-plan.md).

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
| `MaxPriceLockInWaitSecs` / `MaxPriceLockInRetryDelaySecs` | 30 / 2 | How long to wait for the current price to lock in |

**Risk weighting (hours-to-empty ramps)** — pessimism intentionally wider/stronger than optimism

| Key | Default | Meaning |
|---|---|---|
| `PessimismStartHours` | 20 | Runway below which pessimism begins ramping |
| `PessimismMaxAtHours` | 4 | Runway at/below which pessimism is maxed |
| `PessimismMaxWeight` | 0.7 | Max pessimism blend fraction |
| `OptimismStartHours` | 26 | Runway above which optimism begins ramping |
| `OptimismMaxAtHours` | 32 | Runway at/above which optimism is maxed |
| `OptimismMaxWeight` | 0.3 | Max optimism blend fraction |

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
- `Clients/AmberClient/Extensions/BaseIntervalExtensionsTests.cs` — interval price helpers.
