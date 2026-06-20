using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Extensions;
using src.apps.HassModel.Battery.Models;

namespace src.apps.HassModel.Battery;

/// <summary>
/// Pure battery planning logic with no Home Assistant dependencies, so it can be unit tested.
/// <see cref="BatteryControl"/> reads/writes Home Assistant and delegates the decision making here.
/// </summary>
public static class BatteryPlanner
{
    /// <summary>
    /// Projects a list of <see cref="EnergySegment"/>s forward from the current charge, applying the
    /// per-segment household usage (drain) and the solar forecast (charge), and tagging each segment
    /// with its Amber buy/sell prices. Segments are produced until prices run out and the minimum
    /// forecast horizon has been reached.
    /// </summary>
    public static List<EnergySegment> BuildSegments(
        DateTime startUtc,
        decimal currentChargeKwh,
        decimal averageSegmentUsage,
        Dictionary<DateTime, int>? solarForecast,
        List<BaseInterval>? amberPrices,
        BatteryConfig config)
        => BuildSegments(startUtc, currentChargeKwh, _ => averageSegmentUsage, solarForecast, amberPrices, config);

    /// <summary>
    /// As above, but drains each segment by a per-segment usage estimate. <paramref name="segmentUsage"/>
    /// is queried with the UTC start of the segment being drained (the elapsed interval), so a
    /// time-of-day estimate produces a realistically shaped trajectory (e.g. evening peaks, overnight
    /// flats) rather than a single flat drain.
    /// </summary>
    public static List<EnergySegment> BuildSegments(
        DateTime startUtc,
        decimal currentChargeKwh,
        Func<DateTime, decimal> segmentUsage,
        Dictionary<DateTime, int>? solarForecast,
        List<BaseInterval>? amberPrices,
        BatteryConfig config)
    {
        var curEnergySegment = new EnergySegment
        {
            EstimatedBatteryChargeKwh = currentChargeKwh,
            Duration = config.SegmentSize,
            StartUtc = startUtc,
            UsageKwh = segmentUsage(startUtc)
        };
        curEnergySegment.ApplySolarForecast(solarForecast);
        curEnergySegment.ApplyPrice(amberPrices);
        var energySegments = new List<EnergySegment>
        {
            curEnergySegment
        };
        while (curEnergySegment.BuyPricePerKw is not null ||
               curEnergySegment.SellPricePerKw is not null ||
               curEnergySegment.StartUtc < startUtc + TimeSpan.FromHours(config.MinForecastHours))
        {
            var nextStartUtc = curEnergySegment.StartUtc + config.SegmentSize;
            curEnergySegment = new EnergySegment
            {
                EstimatedBatteryChargeKwh = curEnergySegment.EstimatedBatteryChargeKwh - segmentUsage(curEnergySegment.StartUtc),
                Duration = config.SegmentSize,
                StartUtc = nextStartUtc,
                UsageKwh = segmentUsage(nextStartUtc)
            };
            curEnergySegment.ApplySolarForecast(solarForecast);
            curEnergySegment.ApplyPrice(amberPrices);
            energySegments.Add(curEnergySegment);
        }
        return energySegments;
    }

    /// <summary>
    /// Greedy solver: while the projected charge crosses a capacity limit, mark the best-priced
    /// segment in the affected window as Sell (above max) or Buy (below min), re-simulate the charge
    /// forward, and repeat until the projection stays within [MinCapacity, MaxCapacity].
    /// Mutates <paramref name="energySegments"/> in place (Action and EstimatedBatteryChargeKwh).
    ///
    /// The limits are detected (<see cref="CalculateBoundaryResult"/>) at MinCapacity/MaxCapacity, but a
    /// solver action reserves a ONE-STEP buffer: a buy may charge only up to MaxCapacity - one segment,
    /// and a sell may discharge only down to MinCapacity + one segment. Otherwise a charge could land the
    /// projection exactly on MaxCapacity (the sell trigger) and a discharge exactly on MinCapacity (the
    /// buy trigger), so a single action would directly arm the opposite action a step later — the
    /// buy/sell loop. With the buffer the battery can reach a trigger only from solar (over max) or usage
    /// (under min), never from the solver's own action.
    /// </summary>
    public static void OptimiseSegments(List<EnergySegment> energySegments, BatteryConfig config, decimal hourlyUsage)
    {
        var boundaryResult = CalculateBoundaryResult(energySegments, config);
        var loopCount = 0;
        while (boundaryResult.IsOutOfBounds && loopCount < energySegments.Count)
        {
            var previousBoundaryCrossingIndex = GetPreviousBoundaryCrossingIndex(energySegments, boundaryResult, config);
            loopCount++;
            if (boundaryResult.IsMax == true)
            {
                var maxPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        config.MinCapacity + config.SegmentDischargeAmountKwh <= (segment.EstimatedBatteryChargeKwh - config.SegmentDischargeAmountKwh) &&
                        previousBoundaryCrossingIndex <= index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing)
                    .MaxBy(segment => segment.GetWeightedPrice(false, config, hourlyUsage));
                if (maxPriceSegment is null)
                    break;
                var maxPriceSegmentIndex = energySegments.IndexOf(maxPriceSegment);
                maxPriceSegment.Action = EnergySegmentAction.Sell;
                maxPriceSegment.ActionReason = EnergySegmentActionReason.Usage;
                // Forcing discharge caps the battery's net change for this segment at the inverter's rate,
                // so the segment's own natural flow is subsumed (its solar exports and its load is served
                // from the discharge, both within the cap). The projection already baked that flow in, so
                // remove it: a high-usage segment now drops by exactly the discharge amount, not amount + usage.
                var dischargeDelta = config.SegmentDischargeAmountKwh + maxPriceSegment.NaturalChargeDeltaKwh;
                for (var i = maxPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh -= dischargeDelta;
                }
            }
            if (boundaryResult.IsMax == false)
            {
                var lowestPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        (segment.EstimatedBatteryChargeKwh + config.SegmentChargeAmountKwh) <= config.MaxCapacity - config.SegmentChargeAmountKwh &&
                        previousBoundaryCrossingIndex <= index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing &&
                        !segment.IsDemandWindow)
                    .MinBy(segment => segment.GetWeightedPrice(true, config, hourlyUsage));
                if (lowestPriceSegment is null)
                    break;
                var lowestPriceSegmentIndex = energySegments.IndexOf(lowestPriceSegment);
                lowestPriceSegment.Action = EnergySegmentAction.Buy;
                lowestPriceSegment.ActionReason = EnergySegmentActionReason.Usage;
                // Forcing charge caps the battery's net change for this segment at the inverter's rate, so
                // the segment's own natural flow is subsumed (its solar surplus and load are met within the
                // cap via the grid). The projection already baked that flow in, so remove it: a sunny segment
                // now rises by exactly the charge amount, not amount + solar surplus.
                var chargeDelta = config.SegmentChargeAmountKwh - lowestPriceSegment.NaturalChargeDeltaKwh;
                for (var i = lowestPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh += chargeDelta;
                }
            }
            boundaryResult = CalculateBoundaryResult(energySegments, config);
        }
    }

    /// <summary>
    /// Opportunistic price arbitrage (buy import low / export high). Runs AFTER <see cref="OptimiseSegments"/>
    /// on the same segment list and mutates it in place. No-op when <c>config.ArbitrageEnabled</c> is false.
    ///
    /// Greedy loop:
    ///  1. Among segments with <c>Action == None</c> and a sell price (<c>SellPricePerKw != null</c>), take the
    ///     one with the highest face-value (predicted) earning.
    ///  2. Among segments with <c>Action == None</c> and a buy price (<c>BuyPricePerKw != null</c>), other than
    ///     the chosen sell, that form a FEASIBLE pair, take the one giving the best net profit per kWh.
    ///  3. Commit the pair only when it clears the profit gate:
    ///       <c>net := sellEarning - buyCost / config.RoundTripEfficiency >= config.ArbitrageMinMarginPerKwh</c>.
    ///     Committing sets <c>buy.Action = Buy</c>, <c>sell.Action = Sell</c>, and applies the 1 kWh round-trip to
    ///     the projection: <c>+SegmentChargeAmountKwh</c> from the buy index onward and
    ///     <c>-SegmentDischargeAmountKwh</c> from the sell index onward (same convention as OptimiseSegments).
    ///  4. Repeat until no profitable, feasible pair remains. If the best sell has no profitable feasible buy,
    ///     move on to the next-best sell before stopping (don't give up at the first miss).
    ///
    /// PESSIMISM IS DIRECTIONAL: the discount (<c>config.ArbitragePessimismWeight</c>) is applied to the LATER
    /// leg of the pair only — the speculative price we're waiting on — while the earlier, more-imminent leg is
    /// priced at face value (the same way a materialised "now" price is acted on at face value elsewhere). That
    /// keeps a near-future leg priced consistently with the current segment, so a sustained run does not drop
    /// out one segment ahead and re-commit each tick:
    ///  - buy before sell: the sell (later) is pessimised, the buy (earlier) is priced at predicted.
    ///  - sell before buy: the buy (later) is pessimised, the sell (earlier) is priced at predicted.
    ///
    /// FEASIBILITY keeps the round-trip within [MinCapacity, MaxCapacity] (i.e. inside the slack between
    /// boundary crossings), using a small tolerance (e.g. 0.01) to absorb SegmentChargeAmountKwh rounding:
    ///  - buy before sell (charge cheap, hold, export dear): every segment i with buyIndex &lt;= i &lt; sellIndex
    ///    must satisfy <c>charge[i] + SegmentChargeAmountKwh &lt;= MaxCapacity + tol</c>.
    ///  - sell before buy (export from stored charge, refill cheap): every segment i with sellIndex &lt;= i &lt; buyIndex
    ///    must satisfy <c>charge[i] - SegmentDischargeAmountKwh &gt;= MinCapacity - tol</c>.
    ///
    /// Round-trip efficiency is applied ONLY in the profit gate, not in the 1 kWh charge accounting.
    /// </summary>
    public static void ApplyArbitrage(List<EnergySegment> energySegments, BatteryConfig config)
    {
        if (!config.ArbitrageEnabled) return;
        const decimal tol = 0.01m;
        var loopGuard = 0;
        while (loopGuard++ < energySegments.Count)
        {
            // Rank candidate sells (Action==None, SellPricePerKw != null) by face-value (predicted) earning,
            // DESC. The pessimism discount is applied per-pair to the later leg only, so the sell is ranked at
            // face value here rather than pre-discounted.
            var sells = energySegments
                .Where(s => s.Action == EnergySegmentAction.None && s.SellPricePerKw != null)
                .OrderByDescending(s => s.WeightedPrice(isBuy: false, 0m));

            var committed = false;
            foreach (var sell in sells)
            {
                var sellIndex = energySegments.IndexOf(sell);

                // Candidate buys: Action==None, BuyPricePerKw != null, not the sell, not a demand window
                // (buying in a demand window incurs extra charges, so it's disallowed — selling is fine),
                // and the pair is feasible. Pessimise only the LATER leg (the speculative price we're waiting
                // on); price the earlier, more-imminent leg at face value. Keep the buy with the best net.
                EnergySegment? bestBuy = null;
                var bestNet = decimal.MinValue;
                foreach (var buy in energySegments.Where(b => b.Action == EnergySegmentAction.None && b.BuyPricePerKw != null && b != sell && !b.IsDemandWindow))
                {
                    var buyIndex = energySegments.IndexOf(buy);
                    if (!FeasiblePair(buyIndex, sellIndex, energySegments, config, tol)) continue;
                    var sellIsLater = sellIndex > buyIndex;
                    var sellEarning = sell.WeightedPrice(isBuy: false, sellIsLater ? config.ArbitragePessimismWeight : 0m);
                    var buyCost = buy.WeightedPrice(isBuy: true, sellIsLater ? 0m : config.ArbitragePessimismWeight);
                    var net = sellEarning - buyCost / config.RoundTripEfficiency;
                    if (net > bestNet) { bestNet = net; bestBuy = buy; }
                }
                if (bestBuy is null) continue; // This sell has no feasible buy -> try next-best sell

                // Profit gate: net profit per kWh must clear the configured margin.
                if (bestNet >= config.ArbitrageMinMarginPerKwh)
                {
                    var buyIndex = energySegments.IndexOf(bestBuy);
                    bestBuy.Action = EnergySegmentAction.Buy;
                    bestBuy.ActionReason = EnergySegmentActionReason.Arbitrage;
                    sell.Action = EnergySegmentAction.Sell;
                    sell.ActionReason = EnergySegmentActionReason.Arbitrage;
                    // Each forced leg moves the battery by exactly the inverter's per-segment rate; the leg
                    // segment's own natural solar/usage flow is subsumed by that move, so remove it from the
                    // applied delta (it is already in the projection). Same convention as OptimiseSegments.
                    var buyDelta = config.SegmentChargeAmountKwh - bestBuy.NaturalChargeDeltaKwh;
                    var sellDelta = config.SegmentDischargeAmountKwh + sell.NaturalChargeDeltaKwh;
                    for (var i = buyIndex; i < energySegments.Count; i++) energySegments[i].EstimatedBatteryChargeKwh += buyDelta;
                    for (var i = sellIndex; i < energySegments.Count; i++) energySegments[i].EstimatedBatteryChargeKwh -= sellDelta;
                    committed = true;
                    break; // Restart the outer while from scratch
                }
                // else: this sell's best buy isn't profitable -> try the next-best sell
            }
            if (!committed) break; // No profitable feasible pair anywhere -> done
        }
    }

    private static bool FeasiblePair(int buyIndex, int sellIndex, List<EnergySegment> energySegments, BatteryConfig config, decimal tol)
    {
        if (buyIndex < sellIndex)
        {
            // Buy before sell: hold the extra kWh; check we don't charge to within one step of MaxCapacity
            // (the same one-step buffer OptimiseSegments reserves, so arbitrage can't park on the sell trigger).
            for (var i = buyIndex; i < sellIndex; i++)
            {
                if (energySegments[i].EstimatedBatteryChargeKwh + config.SegmentChargeAmountKwh > config.MaxCapacity - config.SegmentChargeAmountKwh + tol)
                    return false;
            }
        }
        else
        {
            // Sell before buy: discharge early then refill; check we don't discharge to within one step of
            // MinCapacity (same one-step buffer, so arbitrage can't park on the buy trigger).
            for (var i = sellIndex; i < buyIndex; i++)
            {
                if (energySegments[i].EstimatedBatteryChargeKwh - config.SegmentDischargeAmountKwh < config.MinCapacity + config.SegmentDischargeAmountKwh - tol)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Finds the first point at which the projected charge is parked against a capacity limit and
    /// still moving the wrong way: at/below MinCapacity and still falling (buy needed), or
    /// at/above MaxCapacity and still rising (sell needed). Returns no boundary when within range.
    /// </summary>
    public static BoundaryResult CalculateBoundaryResult(List<EnergySegment> energySegments, BatteryConfig config)
    {
        for (int i = 1; i < energySegments.Count; i++)
        {
            var curSegment = energySegments[i - 1];
            var nextSegment = energySegments[i];
            if (curSegment.EstimatedBatteryChargeKwh <= config.MinCapacity &&
                nextSegment.EstimatedBatteryChargeKwh < curSegment.EstimatedBatteryChargeKwh)
            {
                return new BoundaryResult
                {
                    IsOutOfBounds = true,
                    IsMax = false,
                    IndexOfBoundaryCrossing = i
                };
            }
            if (config.MaxCapacity <= curSegment.EstimatedBatteryChargeKwh &&
                curSegment.EstimatedBatteryChargeKwh < nextSegment.EstimatedBatteryChargeKwh)
            {
                return new BoundaryResult
                {
                    IsOutOfBounds = true,
                    IsMax = true,
                    IndexOfBoundaryCrossing = i
                };
            }
        }
        return new BoundaryResult
        {
            IsOutOfBounds = false,
            IsMax = null,
            IndexOfBoundaryCrossing = null
        };
    }

    /// <summary>
    /// The UTC start of the first segment whose projected charge drops below MinCapacity, or
    /// <see cref="DateTime.MaxValue"/> if it never does.
    /// </summary>
    public static DateTime GetBatteryUntil(List<EnergySegment> energySegments, BatteryConfig config)
    {
        return energySegments.FirstOrDefault(segment => segment.EstimatedBatteryChargeKwh < config.MinCapacity)?.StartUtc ?? DateTime.MaxValue;
    }

    /// <summary>
    /// Walks back from the boundary crossing to the last segment where an action would already push
    /// the battery against the opposite limit, bounding the window in which a Buy/Sell may be placed.
    /// </summary>
    public static int GetPreviousBoundaryCrossingIndex(List<EnergySegment> energySegments, BoundaryResult boundaryResult, BatteryConfig config)
    {
        if (boundaryResult.IndexOfBoundaryCrossing is null || boundaryResult.IsMax is null) return 0;
        for (var i = boundaryResult.IndexOfBoundaryCrossing.Value; i >= 0; i--)
        {
            var curSegment = energySegments[i];
            if (boundaryResult.IsMax.Value
                    ? (curSegment.EstimatedBatteryChargeKwh - config.SegmentDischargeAmountKwh) <= config.MinCapacity
                    : config.MaxCapacity <= (curSegment.EstimatedBatteryChargeKwh + config.SegmentChargeAmountKwh))
            {
                return i;
            }
        }
        return 0;
    }
}
