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
        var advancedPriceWeight = -advancedPriceWeightMultiplier * config.AdvancedPriceWeight;
        
        if (isBuy && !segment.IsBuyEstimate) return segment.BuyPricePerKw ?? decimal.MaxValue;
        if (!isBuy && !segment.IsSellEstimate) return segment.SellPricePerKw ?? decimal.MinValue;
        var advancedPrice = isBuy ? segment.AdvancedBuyPrice : segment.AdvancedSellPrice;
        if (advancedPrice is null) return isBuy
            ? segment.BuyPricePerKw * (1 + Math.Max(0, advancedPriceWeight)) ?? decimal.MaxValue
            : segment.SellPricePerKw * (1 - Math.Max(0, advancedPriceWeight)) ?? decimal.MinValue;
        var predicted = isBuy ? advancedPrice.Predicted : -advancedPrice.Predicted;
        var high = isBuy ? advancedPrice.High : -advancedPrice.High;
        var low = isBuy ? advancedPrice.Low : -advancedPrice.Low;
        
        var advancedPriceAdjustor = advancedPriceWeight > 0 ? high : low;
        return predicted * (1 - Math.Abs(advancedPriceWeight)) +
               advancedPriceAdjustor * Math.Abs(advancedPriceWeight);
    }
}