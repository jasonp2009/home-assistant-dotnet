using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery.Clients.AmberClient.Enums;
using src.apps.HassModel.Battery.Clients.AmberClient.Extensions;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
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

    public static void ApplyPrice(this EnergySegment segment, List<BaseInterval>? priceIntervals)
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
            segment.IsDemandWindow = buyIntervalWithOverlap?.interval?.TariffInformation?.DemandWindow ?? false;
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
}