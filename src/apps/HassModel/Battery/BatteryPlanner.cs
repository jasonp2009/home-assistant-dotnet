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
                for (var i = lowestPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh += config.SegmentChargeAmountKwh;
                }
            }
            boundaryResult = CalculateBoundaryResult(energySegments, config);
        }
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
