# Arbitrage fix + self-review (2026-08-17)

Follow-up to the arbitrage audit of 2026-08-12. This document records what was changed, what was
measured, and — deliberately at length — what is **weak** about it. Read the "Where this is weak"
section before trusting any number here.

## What changed

Three changes, all in the arbitrage path. None touches the boundary solver.

### 1. Sell-before-buy legs priced no more optimistically than the boundary solver prices them

[`BatteryPlanner.LegWeight`](../src/apps/HassModel/Battery/BatteryPlanner.cs) returns
`max(flatArbitrageWeight, GetRiskWeight(runway))` and is applied to both legs of a **sell-before-buy**
pair. `ApplyArbitrage` now takes `hourlyUsage` to compute the runway.

The audit found the two passes valuing the *same* segment differently: arbitrage priced the 08-12 11:00
refill at **8.85c**, the boundary solver at **10.87c**. Since the gate was `sell − buy/η ≥ 2c`, that 2c
gap is the whole difference between committing and not:

| refill priced by | net | vs 2c margin |
|---|---|---|
| flat `ArbitragePessimismWeight` | 14 − 8.85/0.9 = 4.17c | commits |
| `max(flat, runway)` | 14 − 10.87/0.9 = 1.92c | blocked |

**`max`, not substitution.** `GetRiskWeight` goes *negative* at deep runway (optimism, to
−`OptimismMaxWeight`). Deferring to it unconditionally would price a full-battery midday refill *below*
predicted and make arbitrage **more** aggressive — the opposite of the intent. This was a real bug in the
first version of this change, caught before it landed.

### 2. Feasibility floor scales with the drain the pair holds through

`FeasiblePair`'s sell-before-buy branch now requires
`level ≥ MinCapacity + step + k · Σ(projected drain from the sell to this segment)`, with
`k = ArbitrageHoldDrainReserveFraction` (deployed 1.0).

The fixed one-step buffer (0.83 kWh) is a *structural* guard — it stops arbitrage parking the projection
on the buy trigger — and was never sized as a forecast-error margin. But it was the only thing standing
between the pair and the floor, and it does not grow with hold length. A pair holding through 15 kWh of
projected drain was judged on the same margin as one holding through 0.3 kWh.

The reserve accumulates *from the sell* rather than being sized on the whole window, so a short hold
reserves almost nothing and a long one reserves in proportion. `k` **is** the tolerance: `k = 1.0` means
"this pair must still clear the floor if the drain runs 2× the estimate".

### 3. Action-reversal cooldown

[`BatteryPlanner.ApplyActionReversalCooldown`](../src/apps/HassModel/Battery/BatteryPlanner.cs)
suppresses an action that is the opposite of the last one committed, within
`ActionReversalCooldownSegments` (deployed 3). Targets the observed 2026-08-06 thrash: **Buy 25c → Sell
24c → Buy 26c** on consecutive segments.

The rule is deliberately an *actuation*-level guard, not a planning one — the planner re-derives
everything every 5 minutes and holds no memory, so cross-run oscillation is invisible from inside a
single plan. State lives on `BatteryControl`; `BatteryPlanner` stays pure. Two asymmetries, both chosen
so the guard can only ever be safe:

- It only ever downgrades to `None`. It can never force a discharge to continue.
- A `Usage` (floor-defence) **buy** is never blocked — refusing to charge can strand the battery on the
  floor, which is the exact harm being fixed. Blocking a max-boundary *sell* is safe because the inverter
  exports surplus at 100% SoC anyway rather than curtailing.

## Measured effect

From `BatteryArbitrageAuditTests`, driving the real pipeline on the deployed config:

| scenario | before | after |
|---|---|---|
| 08-12 06:25 morning sell (the trade that lost money) | `Sell 06:25@14c → Buy 11:20@8c` | **no arbitrage legs** |
| 08-12 18:15 evening buy-before-sell | 5 pairs | **5 pairs, unchanged** |
| reserve over a 3-segment hold | — | 0.57 kWh |
| reserve over a 59-segment hold | — | 15.13 kWh |

Full suite: **158 passed, 7 failed** — the same 7 pre-existing `BatteryScenarioTests*` known-issue probes
for the boundary solver, failing identically before this work.

## `ArbitrageMinMarginPerKwh` was NOT raised

The owner called this a last resort, so it was measured rather than turned. Changes 1 and 2 block the
loss-making trade on their own, so the margin stays at 2c. If out-of-sample results later show
sell-before-buy still losing, this is the next lever — it is the only term that could price the ~4c
structural feed-in/import spread and battery degradation, neither of which is modelled anywhere.

## Where this is weak

### The headline audit number does not replicate out-of-sample

The audit's "−59c, negative every day" came from 2026-08-05→12. Re-running the same marginal-cost
calculation on the **following five days** (08-12→17), which were not used to design the fix:

| day | arbitrage sold | at | vs dearest-import replacement | net |
|---|---|---|---|---|
| 08-12 | 1.66 kWh | 15.0c | 28c | **−3c** |
| 08-14 | 18.32 kWh | 19.0c | 310c | **+38c** |

Net **+35c**, not negative. So "arbitrage loses money" as a blanket claim is **not supported**. Two
five-to-seven day windows disagreeing means the sample is too small for either sign to be trusted.

What *does* survive the disagreement is the **directional** split, and it is the thing this fix is built
on:

- 08-14 (+38c) is a **buy-before-sell** day — bought ~35 kWh at 10–16c from 08:00 to 14:35 (SoC 14% →
  92%), then sold ~18 kWh at 18–19c from 16:25 to 18:30. The fix deliberately does not touch this
  direction, and the tests pin that it survives.
- 08-12 19:20 (−3c) is a **sell-before-buy**, the direction the fix targets.

That is encouraging, but note the caveat below — I inferred the direction from the SoC/price pattern
because the app does not log which direction a pair was.

### `k = 1.0` is calibrated on a single morning

It is set from the one measurement I have: the drain on 2026-08-12 ran roughly 2× the time-of-day
estimate through the morning. That is one day, one household, one season. The diagnostic showed
`k = 0.75` is where this specific trade flips, so **1.0 is only 33% above the value that makes the test
pass** — uncomfortably close to having been tuned to the test. I chose 1.0 because it has an independent
justification ("survive the error actually observed"), not because it was the smallest number that
worked, but a second season of data could easily move it.

### The reserve haircuts drain but ignores solar

This is the most substantive gap. The diagnostic over the 08-12 morning hold window shows the projection
carries **15.2 kWh of drain against 10.6 kWh of forecast solar** — so a large part of what makes the hold
*look* safe is solar that has not arrived yet. A reserve that inflates drain but takes forecast solar at
face value is only covering half the exposure. Worse, the deployed log for that very day shows
`sensor.pawar_plant_total_energy_today` reading `unavailable`, so the Forecast.Solar `actual`
recalibration was silently skipped.

A `k_solar` haircut alongside `k_drain` would be more principled; the diagnostic showed
`k_drain=0.5 + k_solar=0.25` also blocks the trade. I did not add it — it is a second knob with no
measurement behind it, and I would rather ship one justified parameter than two guessed ones. **This is
the first thing I would do next.**

### The cooldown's wiring is untested

`ApplyActionReversalCooldown` is pure and has 9 unit tests. The code that *drives* it —
`BatteryControl.ApplyReversalCooldown`, which tracks `_lastCommittedAction` and computes segments-elapsed
— has none, because `BatteryControl` needs an `IHaContext` and the test project has no harness for it.
The arithmetic is trivial and the `DateTime.MinValue` path is guarded by the `None` check, but "trivial
and guarded" is exactly what untested wiring always looks like.

Two known behaviours of that wiring, neither covered by a test:

- On restart the cooldown does not bite until the process commits its first action. Safe (it can only
  ever permit something the planner already wanted), but it means a restart loop would defeat it entirely.
- When the cooldown fires, the segment's projected charge still carries the un-taken action's delta for
  the rest of that run, and the suppressed leg's *pair* remains in the plan — so the "Arbitrage legs"
  log line can show an orphaned buy. Self-healing (the next run rebuilds from measured SoC) but the log
  is momentarily misleading.

### An asymmetry I introduced

Sell *ranking* applies `LegWeight` unconditionally, while pair *pricing* applies it only to
sell-before-buy. Ranking happens before a direction is known, so this is unavoidable without restructuring
the loop — but it means a sell on a low-SoC segment is de-prioritised even when it lands in a
buy-before-sell pair priced at the flat weight. It affects the **order** sells are tried, never whether a
pair is admissible. Documented in the code rather than left as an accident.

### Method: the reason log is sparse

Attribution of runs to Usage vs Arbitrage relies on `input_select.current_action_reason_battery_log`,
which is only written when the reason is Usage or Arbitrage and otherwise carries the previous value
forward. It changed **6 times in 5 days**. Every per-reason number in the audit and in this document
inherits that coarseness. The pair direction is not logged at all and had to be inferred.

## Was the fixture shaped to flatter the fix?

`BatteryArbitrageAuditTests` uses a synthetic 42 h price/usage/solar shape that I wrote, which is a real
risk of circularity. Mitigations, and their limits:

- Prices, the usage profile and the SoC are read off the deployed logs and price sensors for 2026-08-12;
  the Amber advanced-band half-widths (1.7c high−predicted, 1.5c predicted−low) are **medians measured
  from the live API**, not invented. An earlier version of the fixture used guessed ±6c bands and did not
  reproduce production behaviour at all — that discrepancy is what exposed the guess.
- `MorningSell_AgainstADistantRefill_BlockedByTheDrainReserve` asserts the **pre-fix** config still
  commits the trade. If the fixture drifts into no longer reproducing the defect, that assertion fails
  rather than the test silently passing for the wrong reason.
- `BatteryArbitrageHoldReserveTests` carries three controls — short hold, low-drain long hold, and
  reserve-disabled — so "long hold rejected" cannot pass merely because the guard rejects everything.
- The solar curve is a plain sine and is **not** calibrated against anything. Given solar turns out to be
  the dominant unmodelled exposure (above), this is the fixture's weakest component.

## Test review

- **Pre-existing sell-side band fixtures are correctly signed.** Every `advSell` band across the suite
  follows the real API convention (low least-negative, high most-negative): `(-4,-40,-44)`,
  `(-10,-27,-40)`, `(-5,-10,-15)`, etc. The inversion recorded in the project history is genuinely fixed,
  and `WeightedPrice`'s `pessimisticBound = -Low` is correct — re-confirmed against the live API this
  session (`low -7.4, pred -7.8, high -8.2`).
- **`ArbitrageNeverValuesARefillBelowWhatTheSolverWouldPay`** checks all 287 candidate refill segments in
  the horizon rather than the one that happened to be picked, so it asserts the invariant rather than an
  instance.
- **`KnownGap_LockedNowSell_StillOutranksAnIdenticalFutureSell`** pins a defect that was *not* fixed (see
  below) so it stays visible. It is written to fail loudly with an instruction to update it if the cliff
  is ever addressed.
- **Weakest test:** `DrainReserve_ScalesWithHoldLength_NotAppliedFlat` asserts on the reserve arithmetic
  rather than on a pair decision, so it would not catch the reserve being computed correctly and then
  applied to the wrong branch. `BatteryArbitrageHoldReserveTests` covers that gap, which is why both exist.

## Deliberately not done

- **The locked/estimate ranking cliff.** `WeightedPrice` returns locked prices raw while every forecast
  leg is discounted, so the current segment always outranks an identical future one (measured: locked 15c
  ranks 15c, an identical forecast 15c ranks 14.30c). This is why arbitrage sells "now" rather than at the
  peak it planned for. Fixing it means changing how *both* passes treat the now/next boundary, which the
  project history shows has been reverted before for causing log oscillation. Pinned by a test, not fixed.
- **Battery degradation cost** — still modelled nowhere. A 53 kWh battery cycling for a 2c/kWh margin is
  plausibly value-destroying regardless of everything above.
- **The ~4c structural feed-in/import spread** — every arbitrage sell in both windows executed 4–10c below
  the simultaneous import price. The gate compares prices across *times* and never sees this.
- **`ArbitrageMinMarginPerKwh`** — left at 2c, per the measurement above.
- The **usage-estimate double-count** recorded in project notes is on a different branch and was not in
  scope here.
