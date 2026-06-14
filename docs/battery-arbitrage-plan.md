# Battery price-arbitrage plan (finding #4)

Status: **plan for review — not yet implemented.**

## Context

Today the planner ([`BatteryPlanner`](../src/apps/HassModel/Battery/BatteryPlanner.cs)) is purely
*boundary-driven*: it only charges/discharges to keep the projected battery within `[MinCapacity, MaxCapacity]`.
It never acts on a price *spread*, so it leaves money on the table when prices swing (e.g. cheap off-peak
import vs an expensive feed-in spike). This adds an **opportunistic buy-low / sell-high** layer on top of the
boundary solver. It is deliberately **conservative** — it only commits a pair when confident it's profitable.

Self-consumption is the default (`Action == None` = Self-consumption mode), so this feature is purely the
explicit **grid-to-grid** trade: buy from the grid (General/import) at a low price, export to the grid (FeedIn)
at a high price.

## Decisions (confirmed with owner)
- **Value model:** grid-to-grid. Profit per kWh ≈ pessimistic FeedIn sell − pessimistic General buy − losses.
- **Profit gate:** commit only if `pessimisticSell ≥ pessimisticBuy / RoundTripEfficiency + ArbitrageMinMargin`.
- **Pessimism:** a **configurable weight** leaning buy → advanced *High*, sell → advanced *Low* (earn-less),
  independent of the runway weight used elsewhere.

## Pricing: pessimistic arbitrage prices (reuse the existing blend)

The existing [`GetWeightedPrice`](../src/apps/HassModel/Battery/Extensions/EnergySegmentExtensions.cs) already
blends a price toward a bound by a signed weight, with the verified buy/sell sign handling. Refactor so the
*blend* is reusable with an explicit weight:

- Extract `decimal WeightedPrice(this EnergySegment s, bool isBuy, decimal weight)` (the current body minus the
  runway-weight derivation).
- `GetWeightedPrice(s, isBuy, config, hourlyUsage)` becomes `WeightedPrice(s, isBuy, GetRiskWeight(...))` — no
  behaviour change.
- Arbitrage prices use a **fixed positive (pessimistic) weight** = `config.ArbitragePessimismWeight`:
  - `pessimisticBuyCost(s)  = WeightedPrice(s, isBuy: true,  +w)`  → leans toward advanced High (costs more).
  - `pessimisticSellEarn(s) = WeightedPrice(s, isBuy: false, +w)`  → leans toward the lowest plausible earning.
  - Locked (non-estimate) prices pass through unchanged (certain); estimates without an advanced band fall back
    to the raw price scaled by the weight. (A positive weight = pessimism on *both* sides — same machinery,
    no new sign logic.)

## Algorithm: `ApplyArbitrage(List<EnergySegment> segments, BatteryConfig config)`

Runs **after** `OptimiseSegments` (safety/required actions first; arbitrage fills the leftover slack). Greedy,
mirroring the owner's sketch:

```
loop:
  bestSell = segments where Action==None and has a sell price, MaxBy(pessimisticSellEarn)
  if bestSell is null: break
  // candidate buys around bestSell, bounded by the nearest min/max crossings (the slack region),
  // Action==None, has a buy price, != bestSell:
  bestBuy = candidatesInSlackRange(bestSell), MinBy(pessimisticBuyCost), that also keeps the
            whole span within [Min, Max] when the ±1 kWh round-trip is applied
  if bestBuy is null: drop bestSell from consideration and continue
  if pessimisticSellEarn(bestSell) >= pessimisticBuyCost(bestBuy) / RoundTripEfficiency + ArbitrageMinMargin:
      commit: bestBuy.Action=Buy, bestSell.Action=Sell; apply +1 kWh from buy onward and −1 kWh from sell onward
      continue
  else:
      break   // the best sell's best buy isn't profitable → stop
```

**Pair orderings** (your "forward and backward"):
- **Buy before sell** — charge cheap, hold, export dear. The held +1 kWh must fit: `charge+1 ≤ Max` for every
  segment between buy and sell.
- **Sell before buy** — export dear from stored charge, refill cheap. Needs `charge−1 ≥ Min` for every segment
  between sell and buy.

The bound check is exactly what keeps a pair inside the slack *between* boundary crossings — a pair spanning a
crossing fails it and is rejected. (Equivalent to your "up to boundary crossings" range.)

## New config (`BatteryConfig.cs` + `BatteryControl.yaml`)

| Key | Suggested default | Meaning |
|---|---|---|
| `ArbitragePessimismWeight` | 0.7 | How far arbitrage prices lean to High (buy) / Low (sell). |
| `RoundTripEfficiency` | 0.9 | Charge→discharge efficiency, used only in the profit gate. |
| `ArbitrageMinMarginPerKwh` | TBD (tune) | Minimum profit margin (c/kWh) to commit a pair. |
| `ArbitrageEnabled` | true | Master on/off. |

## Integration

In `GetCurrentActionAsync` (via `BatteryControl` → `BatteryPlanner`), after `OptimiseSegments(...)` call
`BatteryPlanner.ApplyArbitrage(energySegments, _config)`. The current segment's resulting action drives the
inverter exactly as today (an arbitrage Buy/Sell on seg0 simply means "charge/export now").

## Testing (mirrors the existing scenario suite)

New `BatteryArbitrageTests.cs` under `test/apps/HassModel/Battery/`, using the same `BatteryTestData` helpers:
- A clear spread (cheap buy, dear sell, both certain) → commits a Buy/Sell pair at the right segments.
- A thin spread that clears raw prices but **fails margin/efficiency** → no commit.
- Uncertain (wide-band) prices → pessimism widens the effective spread, so a marginal pair is **not** committed.
- A pair that would push the span outside `[Min, Max]` → rejected (slack/bounds check).
- Arbitrage never overrides a boundary-solver action; both orderings (buy-before-sell, sell-before-buy) work.

## Open items / simplifications to confirm
- **Efficiency is applied only in the profit gate**, not in the 1 kWh charge accounting (keeps the simple
  integer-kWh segment model). Flag if you want the projection to charge `1/efficiency` to deliver 1 kWh.
- **`ArbitrageMinMarginPerKwh` default** needs tuning against your Amber data.
- Greedy **best-sell-first, 1 kWh pairs**, iterated — simple and matches your sketch; not a global optimum.

## Verification
`dotnet test -c Release` (new arbitrage tests + the existing suite stays green); `dotnet build -c Release`.
Do **not** run the app (it commands the live inverter).
