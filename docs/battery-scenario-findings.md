# Battery control — scenario test findings

Working notes from an overnight pass writing scenario tests against `BatteryPlanner` to find bugs and
sub-optimal decisions. Tests live in `test/apps/HassModel/Battery/`. Probes that intentionally fail are
tagged `[Trait("Category","KnownIssue")]`; run the green subset with
`dotnet test -c Release --filter "Category!=KnownIssue"`.

Sign/units reminder: 5-min segments, 12 kW rates → ~1 kWh moved per segment (decimal arithmetic makes it
1 ± ~1e-15, so charge-bound assertions use a small tolerance). Feed-in earnings are stored POSITIVE in
`SellPricePerKw` (Amber reports feed-in perKwh negative; `ApplyPrice` negates). The runway weight uses
hours-to-empty = (charge − MinCapacity) / hourlyUsage.

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
- Over-charge relief discharges at the **highest** feed-in price (most profitable). *Initially looked like a
  bug, but was a false alarm from inverted test sign — confirmed correct.*
- High solar keeps the battery within MaxCapacity by discharging. *Initially "failed" only due to a decimal
  rounding artifact in the per-segment kWh; not a real over-charge.*

## Confirmed issues (failing probes — for discussion)

### 1. Demand-window-only periods deplete below the minimum reserve
`DemandWindowOnly_ShouldNotLeaveBatteryBelowMinReserve` — when every buy candidate before a min-crossing is
in a demand window, the buy filter (`!segment.IsDemandWindow`) excludes them all, `MinBy` returns null, the
loop `break`s, and the projected charge is left below `MinCapacity`.
- Why: avoiding demand-window charging is hard-coded with no emergency override.
- Fix direction: allow buying in a demand window as a last resort when the battery would otherwise drop
  below the reserve (e.g. retry the buy selection without the demand-window exclusion if the first pass finds
  nothing).

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

### 4. No proactive arbitrage — won't charge at cheap (or negative) prices unless forced
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

### 6. Over-charge relief reaches back and discharges a far-from-full segment early
`FutureOvercharge_DoesNotDischargeFarFromFullSegment` — with the battery at 40 kWh now and a solar-driven
over-charge projected several segments out, the sell window spans `[0, crossing]`, so the solver discharges
the segment with the best feed-in anywhere in that span — here the current 40 kWh segment (feed-in 30c),
dropping it to 39 before solar refills it.
- Why: `GetPreviousBoundaryCrossingIndex` only walks back to where discharging would hit Min, so the sell
  window can be very wide; selection is purely by feed-in price within it.
- Severity: nuanced — this is often *good* arbitrage (sell high now, refill free from solar later), but it
  discharges early based on a forecast and could deplete reserve if the solar forecast under-delivers.
- Fix direction: confirm whether early discharge-for-arbitrage is intended; if not, constrain the sell window
  closer to the actual over-charge, or only discharge segments whose own projected charge is near Max.

<!-- More findings appended below as scenario batches complete. -->
