using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery.Clients.AmberClient.Enums;
using src.apps.HassModel.Battery.Clients.AmberClient.Extensions;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;

namespace src.apps.HassModel.Battery.Extensions;

public static class EnergySegmentExtensions
{
    public static void ApplySolarForecast(this EnergySegment segment, Dictionary<DateTime, int>? solarForecast)
    {
        if (solarForecast is null) return;
        var solarForecastWh = Convert.ToDecimal(solarForecast
            .Where(pair => segment.StartUtc <= pair.Key && pair.Key < segment.EndUtc)
            .Sum(pair => pair.Value));
        var solarForecastKwh = solarForecastWh / 1000;

        segment.SolarForecastKwh = solarForecastKwh;
        segment.EstimatedBatteryChargeKwh += solarForecastKwh;
    }

    public static void ApplyPrice(this EnergySegment segment, List<BaseInterval>? priceIntervals, decimal advancedPriceWeight)
    {
        if (priceIntervals is null) return;
        
        var buyIntervals = priceIntervals
            .Where(interval => interval.ChannelType is ChannelType.General or ChannelType.ControlledLoad)
            .Select(interval => (GetOverlapDuration(interval.StartTime, interval.EndTime, segment.StartUtc, segment.EndUtc), interval))
            .Where(intervalWithOverlap => intervalWithOverlap.Item1 != TimeSpan.Zero)
            .ToList();

        if (buyIntervals.Count > 0)
        {
            var buyIntervalWithOverlap= buyIntervals?.MaxBy(intervalWithOverlap => intervalWithOverlap.Item1);
            segment.BuyPricePerKw = buyIntervalWithOverlap?.interval.GetPrice();
            segment.AdvancedBuyPrice = buyIntervalWithOverlap?.interval.GetAdvancedPrice();
            segment.IsDemandWindow = buyIntervalWithOverlap?.interval?.TariffInformation?.DemandWindow ?? false;
            segment.IsBuyEstimate = buyIntervalWithOverlap?.interval?.IsEstimate() ?? true;
        }
        
        var sellIntervals = priceIntervals
            .Where(interval => interval.ChannelType is ChannelType.FeedIn)
            .Select(interval => (GetOverlapDuration(interval.StartTime, interval.EndTime, segment.StartUtc, segment.EndUtc), interval))
            .Where(intervalWithOverlap => intervalWithOverlap.Item1 != TimeSpan.Zero)
            .ToList();

        if (sellIntervals.Count > 0)
        {
            var sellIntervalWithOverlap= sellIntervals?.MaxBy(intervalWithOverlap => intervalWithOverlap.Item1);
            segment.SellPricePerKw = -sellIntervalWithOverlap?.interval.GetPrice();
            segment.AdvancedSellPrice = sellIntervalWithOverlap?.interval.GetAdvancedPrice();
            segment.IsSellEstimate = sellIntervalWithOverlap?.interval?.IsEstimate() ?? true;
        }
    }
    
    private static TimeSpan GetOverlapDuration(DateTime start1, DateTime end1, DateTime start2, DateTime end2)
    {
        // 1. Check if an overlap exists first
        if (start1 > end2 || start2 <= end1 == false) 
        {
            return TimeSpan.Zero; // No overlap
        }

        // 2. Find the intersection boundaries
        DateTime overlapStart = start1 > start2 ? start1 : start2;
        DateTime overlapEnd = end1 < end2 ? end1 : end2;
        TimeSpan overlapDuration = overlapEnd - overlapStart;
        return overlapDuration;
    }

    public static decimal GetWeightedPrice(this EnergySegment segment, bool isBuy, BatteryConfig config)
    {
        var capacityDiff = config.MaxCapacity - config.MinCapacity;
        var batteryMidpointKwh = (config.MaxCapacity + config.MinCapacity) / 2;
        var advancedPriceWeightMultiplier = (segment.EstimatedBatteryChargeKwh - batteryMidpointKwh)*2/capacityDiff;
        var advancedPriceWeight = - advancedPriceWeightMultiplier * config.AdvancedPriceWeight;
        
        if (isBuy)
        {
            if (!segment.IsBuyEstimate)
            {
                return segment.BuyPricePerKw ?? decimal.MaxValue;
            }
            if (segment.AdvancedBuyPrice is null)
            {
                return segment.BuyPricePerKw * (1 + Math.Max(0, advancedPriceWeight)) ?? decimal.MaxValue;
            }
            if (advancedPriceWeight == 0)
            {
                return segment.AdvancedBuyPrice.Predicted;
            }
            if (advancedPriceWeight > 0)
            {
                return segment.AdvancedBuyPrice.Predicted * (1 - advancedPriceWeight) +
                       segment.AdvancedBuyPrice.High * advancedPriceWeight;
            }
            return segment.AdvancedBuyPrice.Predicted * (1 + advancedPriceWeight) +
                   segment.AdvancedBuyPrice.Low * - advancedPriceWeight;
        }
        if (!segment.IsSellEstimate)
        {
            return segment.SellPricePerKw ?? decimal.MinValue;
        }
        if (segment.AdvancedSellPrice is null)
        {
            return segment.SellPricePerKw * (1 - Math.Max(0, advancedPriceWeight)) ?? decimal.MinValue;
        }
        if (advancedPriceWeight == 0)
        {
            return -segment.AdvancedSellPrice.Predicted;
        }
        if (advancedPriceWeight < 0)
        {
            return -segment.AdvancedSellPrice.Predicted * (1 + advancedPriceWeight) +
                   -segment.AdvancedSellPrice.High * -advancedPriceWeight;
        }
        return -segment.AdvancedSellPrice.Predicted * (1 - advancedPriceWeight) +
               -segment.AdvancedSellPrice.Low * advancedPriceWeight;
    }
    
    public static decimal GetWeightedPrice(this BaseInterval interval, decimal advancedPriceWeight)
    {
        var advancedPrice = interval.GetAdvancedPrice();
        if (advancedPrice is not null)
        {
            return advancedPrice.Predicted * (1 - advancedPriceWeight) +
                   (interval.ChannelType is ChannelType.FeedIn ? advancedPrice.Low : advancedPrice.High) * advancedPriceWeight;
        }
        if (!interval.IsEstimate())
        {
            return interval.GetPrice();
        }
        if (interval.ChannelType is ChannelType.FeedIn)
        {
            return interval.GetPrice() * (1 - advancedPriceWeight / 2);
        }
        return interval.GetPrice() * (1 + advancedPriceWeight);
    }
}