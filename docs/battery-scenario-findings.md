# Battery control — scenario test findings

Working notes from an overnight pass writing scenario tests against `BatteryPlanner` to find bugs and
sub-optimal decisions. Tests live in `test/apps/HassModel/Battery/`. Probes that intentionally fail are
tagged `[Trait("Category","KnownIssue")]`; run the green subset with
`dotnet test -c Release --filter "Category!=KnownIssue"`.

Sign/units reminder: 5-min segments, 12 kW rates → ~1 kWh moved per segment (decimal arithmetic makes it
1 ± ~1e-15, so charge-bound assertions use a small tolerance). Feed-in earnings are stored POSITIVE in
`SellPricePerKw` (Amber reports feed-in perKwh negative; `ApplyPrice` negates). The runway weight uses
hours-to-empty = (charge − MinCapacity) / hourlyUsage.

## TL;DR

85 tests total: **75 pass, 10 fail**. All 10 failures are intentional `KnownIssue` probes (green subset
`dotnet test --filter "Category!=KnownIssue"` is 75/75). After owner triage:

| # | Issue | Severity | Status |
|---|-------|----------|--------|
| 2 | Optimism skips a certain good price when runway is deep | Medium | **Open** |
| 3 | Discharges to relieve an over-charge regardless of economics (negative feed-in, or no feed-in data) | Medium | **Open** |
| 6 | Over-charge relief can discharge a far-from-full segment early (wide sell window) | Low | Open / nuanced |
| 5 | Boundary detection misses a single-step jump past a limit (also: induced over-charge / steep decline) | Low | Open / edge |
| 7 | Charge/discharge step (12×5/60) isn't exactly 1 kWh, nudging detection at the threshold | Low | Open / cleanup |
| 4 | No proactive arbitrage (won't charge at cheap/negative prices) | High | Accepted gap — to add later |
| 1 | No charging during demand windows | — | Intentional — not a bug (grid covers it) |

None of the open items were fixed — they're for review.

## Verified-correct behaviour (passing scenarios)
- Buys at the cheapest segment when depletion forces a buy.
- Low battery (short runway → pessimistic) prefers a certain current price over an optimistically-cheaper
  uncertain estimate. (This is the headline fix from the runway-weighting change.)
- Comfortable battery within bounds takes no action.
- Strong incoming solar (battery rising) → does not buy from grid.
- When a buy is forced, it picks the cheapest early segment over the expensive forced moment.
- Sitting flat exactly at the minimum reserve → no thrashing (no needless buy).
- Demand-window-only-around-a-gap: buys in the one non-demand slot.
- Multi-crossing (overnight min then daytime max) resolves both back into [Min, Max].
- Over-charge relief discharges at the highest feed-in segment within the window.
- Pessimism still picks the cheapest *certain* price (doesn't overpay when a cheaper locked price exists).
- Deep depletion needing ~3 kWh buys at the three cheapest segments.
- Battery starting below the reserve → charges immediately (current action = Buy).
- Short runway prefers a tighter-band estimate over a wider-band one with a slightly lower predicted price.
- End-to-end (BuildSegments → ApplyPrice → OptimiseSegments): charges at the cheap current segment.
- Estimate-sell sign handling: among uncertain feed-in estimates it discharges at the highest *expected* earning.
- Neutral runway band uses the predicted price (ranks two neutral estimates by predicted).
- A segment with no price is never chosen for a buy (weighted price falls back to decimal.MaxValue).
- **Usage drives conservatism** (validates the runway-weighting design): with the same prices/charge, a high
  usage rate (short runway) makes it prefer the certain near price, while a low usage rate (deep runway) leans
  to the optimistic estimate. The low-usage case is the same trade-off as finding #2.

## Questions for review (behaviour to confirm, not necessarily bugs)
- **Selling during demand windows** (`SellDuringDemandWindow_IsAllowed`, passing): the buy filter excludes
  demand windows (intentional), but the sell filter does not — so the planner will discharge/export during a
  demand window. Is that intended, or should demand-window energy be kept for self-consumption to avoid the
  demand charge? Parallel to the buy-side decision.
- Over-charge relief discharges at the **highest** feed-in price (most profitable). *Initially looked like a
  bug, but was a false alarm from inverted test sign — confirmed correct.*
- High solar keeps the battery within MaxCapacity by discharging. *Initially "failed" only due to a decimal
  rounding artifact in the per-segment kWh; not a real over-charge.*

## Confirmed issues (failing probes — for discussion)

### 1. No charging during demand windows — INTENTIONAL (not a bug)
`DemandWindowOnly_DoesNotBuy_ReliesOnGrid` (now a passing test). The planner never charges during a demand
window; if the whole pre-depletion window is demand-windowed it buys nothing and the home draws from the
grid. Confirmed by the owner as intended — demand windows carry excess usage charges, so grid import there is
expected. No change needed.

### 2. Optimism skips a certain good price when runway is deep
`DeepRunway_DoesNotSkipCertainPrice_ForOptimisticEstimate` — with lots of runway early in the buy window, the
optimism discount leans an uncertain estimate toward its Low bound, making a predicted-14c segment score 11c
and beating a certain 12c. It buys the estimate whose *expected* price (14c) is actually worse.
- Why: optimism keys off the Low bound regardless of how far the predicted price sits above the alternative.
- Fix direction: optimism should never discount an estimate below the cheapest *certain* option; or cap the
  optimism adjustment so it can't cross a locked price; or only apply optimism relative to predicted, not Low.

### 3. Over-charge relief discharges even at a negative feed-in price
`Overcharge_DoesNotDischargeAtNegativeFeedIn` — when the battery would exceed max and all feed-in prices are
negative (you pay to export), the solver still places a Sell. Physically the inverter would curtail solar
instead of paying to export.
- Why: the model treats an over-charge as something that must be discharged, ignoring whether discharging is
  economic; there is no "let solar curtail" option.
- Fix direction: don't force a discharge when feed-in is negative (treat curtailment as the action), or only
  discharge to relieve over-charge when the feed-in price is above some floor.
- Related evidence: `Overcharge_DischargesEvenWithNoFeedInData` — with no feed-in price at all on any segment,
  it still discharges to relieve the over-charge (weighted price falls back to decimal.MinValue and a Sell is
  placed anyway). Same root cause: over-charge relief ignores whether discharging is economic.

### 4. No proactive arbitrage — won't charge at cheap (or negative) prices unless forced
**Status: ACCEPTED GAP** — the app doesn't do arbitrage yet; owner plans to add it later (will pair on it).
The two probes below stay red as a spec for that future work.
`Arbitrage_BuysCheapNowWhenNoBoundaryCrossing`, `NegativePrice_ChargesWhenPaidToConsume` — with the battery
mid-range and no capacity-limit crossing, an exceptionally cheap (2c) or even negative (−10c, paid to consume)
price produces NO buy. The planner is purely boundary-driven.
- Why: the only trigger to charge is a projected drop below MinCapacity. Price is used only to choose *where*
  within a forced window, never to act on its own.
- Impact: misses cheap/negative-price charging that would displace later expensive self-consumption — likely
  the biggest source of left-on-the-table savings.
- Fix direction (bigger change): add a price-threshold pass on top of the boundary solver — charge when price
  is below a low percentile and there's headroom; likewise consider discharging when price is very high.

### 5. Boundary detection misses a single-step jump past a limit that then plateaus
`OverchargeJump_StaysWithinMax` (charges 48→52→52→52 → no Sell), `UnderchargeJump_StaysAboveMin`
(10→6→6→6 → no Buy). Detection requires the charge to be *past* the limit AND still moving the wrong way; an
overshoot that immediately plateaus is never flagged.
- Why: `CalculateBoundaryResult` keys off "≥Max and still rising" / "≤Min and still falling".
- Impact: edge case — needs an unrealistically large one-segment swing (gradual ramps pass through the limit
  and are caught) — but the detection is brittle.
- Fix direction: flag any segment whose projected charge is simply outside [Min, Max], rather than only a
  "still-moving" crossing.
- Same root cause, two more manifestations:
  - `BuyToFixMin_InducesUndetectedOvercharge` — a forced buy pushes seg6 to ~51, but the prior segment sits
    at ~Max so the crossing is missed and the over-charge is left unrelieved.
  - `DeepDepletion_SteepDecline_LeavesSegmentBelowMin` — a >1 kWh/segment decline leaves a residual sub-Min
    dip after partial buys (prior segment already raised above Min). Edge: real usage/segment ≪ charge rate.
  - `SharpPeakAndTrough_LeftBadlyOutOfBounds` — the most striking case: a sharp peak (60) and trough (1) the
    projection passes through are left at ~59 / ~1 after a single corrective action de-triggers detection.
    Shows the gap can leave the plan *badly* out of bounds, not just by 1 kWh. (Still needs a sharp,
    unrealistic single-segment swing; gradual ramps are caught while crossing.) → bumps #5 toward Low–Med.

### 7. Charge/discharge step size isn't exactly 1 kWh
`SegmentChargeAmountKwh`/`SegmentDischargeAmountKwh` = `RateKw × Convert.ToDecimal(SegmentSize.TotalHours)`,
and `12 × (5/60)` evaluates to ~`0.99999999999999996`, not `1.0`.
- Impact: charge accounting drifts by ~1e-15 per segment (negligible), but right at a Min/Max threshold it can
  flip a boundary comparison (contributes to finding #5's induced-overcharge case, where seg5 lands at
  ~49.9999 instead of 50 and the crossing is missed).
- Fix direction: compute the step exactly in decimal, e.g. `RateKw * SegmentSizeMins / 60m`.

### 6. Over-charge relief reaches back and discharges a far-from-full segment early
`FutureOvercharge_DoesNotDischargeFarFromFullSegment` — with the battery at 40 kWh now and a solar-driven
over-charge projected several segments out, the sell window spans `[0, crossing]`, so the solver discharges
the segment with the best feed-in anywhere in that span — here the current 40 kWh segment (feed-in 30c),
dropping it to 39 before solar refills it.
- Why: `GetPreviousBoundaryCrossingIndex` walks back from the over-charge to the last segment that is itself
  too low to safely discharge (`charge − dischargeAmount ≤ Min`); the sell window starts just after it. When
  charge stays well above Min across the horizon there is no such segment, so the window reaches back to the
  current segment, and selection within it is purely by feed-in price.
- Severity: nuanced — this is often *good* arbitrage (sell high now, refill free from solar later), but it
  discharges early based on a forecast and could deplete reserve if the solar forecast under-delivers.
- Fix direction: confirm whether early discharge-for-arbitrage is intended; if not, constrain the sell window
  closer to the actual over-charge, or only discharge segments whose own projected charge is near Max.

## Code-review candidates (flagged by reading the code; NOT yet reproduced by a test)
Leads to confirm, not confirmed bugs:
- **General + ControlledLoad lumped for buy** (`ApplyPrice`): both channels feed one buy-candidate list and
  the longer-overlap interval wins, so a segment straddling a tariff boundary could take the wrong channel's
  price / `IsDemandWindow`. Impact depends on whether Amber ever returns both channels for the same slot.
- **Buy window may over-narrow after earlier buys** (`GetPreviousBoundaryCrossingIndex`): it reads the
  already-mutated charges, so once earlier buys raise mid-list charge near Max the walk-back can start later
  than ideal and exclude cheap early segments from a subsequent buy. Plausible; not reproduced.
- **`SegmentSizeMins = 0` would make `BuildSegments` loop forever** (time never advances). Config-guard only.
- A code-review hypothesis that the `loopCount < Count` cap causes non-convergence was investigated and
  **refuted** — the real mechanism behind out-of-bounds plans is finding #5 (detection de-triggers), not the cap.

<!-- More findings appended below as scenario batches complete. -->
