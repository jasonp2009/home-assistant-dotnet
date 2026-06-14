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
    {
        var curEnergySegment = new EnergySegment
        {
            EstimatedBatteryChargeKwh = currentChargeKwh,
            Duration = config.SegmentSize,
            StartUtc = startUtc
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
            curEnergySegment = new EnergySegment
            {
                EstimatedBatteryChargeKwh = curEnergySegment.EstimatedBatteryChargeKwh - averageSegmentUsage,
                Duration = config.SegmentSize,
                StartUtc = curEnergySegment.StartUtc + config.SegmentSize
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
                        config.MinCapacity <= (segment.EstimatedBatteryChargeKwh - config.SegmentDischargeAmountKwh) &&
                        previousBoundaryCrossingIndex <= index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing)
                    .MaxBy(segment => segment.GetWeightedPrice(false, config, hourlyUsage));
                if (maxPriceSegment is null)
                    break;
                var maxPriceSegmentIndex = energySegments.IndexOf(maxPriceSegment);
                maxPriceSegment.Action = EnergySegmentAction.Sell;
                maxPriceSegment.ActionReason = EnergySegmentActionReason.Usage;
                for (var i = maxPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh -= config.SegmentDischargeAmountKwh;
                }
            }
            if (boundaryResult.IsMax == false)
            {
                var lowestPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        (segment.EstimatedBatteryChargeKwh + config.SegmentChargeAmountKwh) <= config.MaxCapacity &&
                        previousBoundaryCrossingIndex <= index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing &&
                        !segment.IsDemandWindow)
                    .MinBy(segment => segment.GetWeightedPrice(true, config, hourlyUsage));
                if (lowestPriceSegment is null)
                    break;
                var lowestPriceSegmentIndex = energySegments.IndexOf(lowestPriceSegment);
                lowestPriceSegment.Action = EnergySegmentAction.Buy;
                lowestPriceSegment.ActionReason = EnergySegmentActionReason.Usage;
                for (var i = lowestPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh += config.SegmentChargeAmountKwh;
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
    ///     one with the highest <c>PessimisticSellEarning(config)</c> (most we can confidently earn exporting).
    ///  2. Among segments with <c>Action == None</c> and a buy price (<c>BuyPricePerKw != null</c>), other than
    ///     the chosen sell, that form a FEASIBLE pair, take the one with the lowest <c>PessimisticBuyCost(config)</c>.
    ///  3. Commit the pair only when it clears the profit gate:
    ///       <c>PessimisticSellEarning(sell) >= PessimisticBuyCost(buy) / config.RoundTripEfficiency + config.ArbitrageMinMarginPerKwh</c>.
    ///     Committing sets <c>buy.Action = Buy</c>, <c>sell.Action = Sell</c>, and applies the 1 kWh round-trip to
    ///     the projection: <c>+SegmentChargeAmountKwh</c> from the buy index onward and
    ///     <c>-SegmentDischargeAmountKwh</c> from the sell index onward (same convention as OptimiseSegments).
    ///  4. Repeat until no profitable, feasible pair remains. If the best sell has no profitable feasible buy,
    ///     move on to the next-best sell before stopping (don't give up at the first miss).
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
            // Candidate sells: Action==None and SellPricePerKw != null, sorted by PessimisticSellEarning DESC
            var sells = energySegments
                .Where(s => s.Action == EnergySegmentAction.None && s.SellPricePerKw != null)
                .OrderByDescending(s => s.PessimisticSellEarning(config));

            var committed = false;
            foreach (var sell in sells)
            {
                var sellIndex = energySegments.IndexOf(sell);
                var sellEarning = sell.PessimisticSellEarning(config);

                // Candidate buys: Action==None, BuyPricePerKw != null, not the sell, and the pair is feasible.
                // Pick the lowest PessimisticBuyCost among feasible buys.
                EnergySegment? bestBuy = null;
                var bestCost = decimal.MaxValue;
                foreach (var buy in energySegments.Where(b => b.Action == EnergySegmentAction.None && b.BuyPricePerKw != null && b != sell))
                {
                    var buyIndex = energySegments.IndexOf(buy);
                    if (!FeasiblePair(buyIndex, sellIndex, energySegments, config, tol)) continue;
                    var cost = buy.PessimisticBuyCost(config);
                    if (cost < bestCost) { bestCost = cost; bestBuy = buy; }
                }
                if (bestBuy is null) continue; // This sell has no feasible buy -> try next-best sell

                // Profit gate
                if (sellEarning >= bestCost / config.RoundTripEfficiency + config.ArbitrageMinMarginPerKwh)
                {
                    var buyIndex = energySegments.IndexOf(bestBuy);
                    bestBuy.Action = EnergySegmentAction.Buy;
                    bestBuy.ActionReason = EnergySegmentActionReason.Arbitrage;
                    sell.Action = EnergySegmentAction.Sell;
                    sell.ActionReason = EnergySegmentActionReason.Arbitrage;
                    for (var i = buyIndex; i < energySegments.Count; i++) energySegments[i].EstimatedBatteryChargeKwh += config.SegmentChargeAmountKwh;
                    for (var i = sellIndex; i < energySegments.Count; i++) energySegments[i].EstimatedBatteryChargeKwh -= config.SegmentDischargeAmountKwh;
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
            // Buy before sell: hold the extra kWh; check we don't exceed MaxCapacity
            for (var i = buyIndex; i < sellIndex; i++)
            {
                if (energySegments[i].EstimatedBatteryChargeKwh + config.SegmentChargeAmountKwh > config.MaxCapacity + tol)
                    return false;
            }
        }
        else
        {
            // Sell before buy: discharge early then refill; check we don't go below MinCapacity
            for (var i = sellIndex; i < buyIndex; i++)
            {
                if (energySegments[i].EstimatedBatteryChargeKwh - config.SegmentDischargeAmountKwh < config.MinCapacity - tol)
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
