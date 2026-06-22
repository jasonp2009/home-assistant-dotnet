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
    // Fallback period length for the final forecast point, which has no following point to measure
    // against. Forecast.Solar's watthours/period endpoint is 15-minute resolution.
    private static readonly TimeSpan DefaultForecastPeriod = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Adds the solar forecast to a segment's projected charge. Forecast.Solar reports energy in
    /// 15-minute periods (the value at key <c>T</c> is the energy generated over <c>[T, nextKey)</c>),
    /// but segments are shorter (5 minutes). Rather than dumping a whole period into the one segment
    /// that happens to contain its timestamp — which sawtooths the trajectory and fools the boundary
    /// solver — each period's energy is spread evenly over its duration and the segment takes only the
    /// fraction that overlaps it.
    /// </summary>
    public static void ApplySolarForecast(this EnergySegment segment, Dictionary<DateTime, int>? solarForecast)
    {
        if (solarForecast is null) return;

        var orderedForecast = solarForecast.OrderBy(pair => pair.Key).ToList();
        var solarForecastWh = 0m;
        for (var i = 0; i < orderedForecast.Count; i++)
        {
            var periodStart = orderedForecast[i].Key;
            var periodEnd = i + 1 < orderedForecast.Count ? orderedForecast[i + 1].Key : periodStart + DefaultForecastPeriod;
            var periodDuration = periodEnd - periodStart;
            if (periodDuration <= TimeSpan.Zero) continue;

            var overlapStart = segment.StartUtc > periodStart ? segment.StartUtc : periodStart;
            var overlapEnd = segment.EndUtc < periodEnd ? segment.EndUtc : periodEnd;
            var overlap = overlapEnd - overlapStart;
            if (overlap <= TimeSpan.Zero) continue;

            solarForecastWh += orderedForecast[i].Value * (decimal)(overlap / periodDuration);
        }
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

    public static decimal GetWeightedPrice(this EnergySegment segment, bool isBuy, BatteryConfig config, decimal hourlyUsageKwh)
    {
        var hoursToEmpty = GetHoursToEmpty(segment.EstimatedBatteryChargeKwh, config.MinCapacity, hourlyUsageKwh);
        return segment.WeightedPrice(isBuy, GetRiskWeight(hoursToEmpty, config));
    }

    /// <summary>
    /// Blends a segment's price toward an uncertainty bound by a signed weight: positive = pessimistic
    /// (buy leans to the advanced High; sell leans to the lowest plausible earning), negative = optimistic.
    /// Locked (non-estimate) prices pass through unchanged. An ESTIMATE with no advanced (ML) band — i.e.
    /// a forecast beyond Amber's ~24h advanced horizon — is IGNORED: it returns the most unattractive value
    /// (max cost for a buy, min earning for a sell) so the greedy selectors never act on a base-perKwh-only
    /// forecast. Shared by the runway weighting (<see cref="GetWeightedPrice"/>) and the arbitrage pricing
    /// (which passes a fixed pessimistic weight).
    /// </summary>
    public static decimal WeightedPrice(this EnergySegment segment, bool isBuy, decimal advancedPriceWeight)
    {
        if (isBuy && !segment.IsBuyEstimate) return segment.BuyPricePerKw ?? decimal.MaxValue;
        if (!isBuy && !segment.IsSellEstimate) return segment.SellPricePerKw ?? decimal.MinValue;
        var advancedPrice = isBuy ? segment.AdvancedBuyPrice : segment.AdvancedSellPrice;
        // No ML band (beyond the advanced horizon): ignore the interval rather than trust the raw forecast.
        if (advancedPrice is null) return isBuy ? decimal.MaxValue : decimal.MinValue;
        var predicted = isBuy ? advancedPrice.Predicted : -advancedPrice.Predicted;
        // The pessimistic bound is the worst case for us, the optimistic bound the best. For a BUY that is
        // the advanced High (dearest import) and Low (cheapest). For a SELL the band is negated into an
        // earning AND the ends swap: the worst earning is at the LOW feed-in price, the best at the HIGH.
        // Amber reports feed-in as negative with High the most negative, so -Low is the lowest earning and
        // -High the highest. A single `High vs Low` pick would be correct for buys but inverted for sells.
        var pessimisticBound = isBuy ? advancedPrice.High : -advancedPrice.Low;
        var optimisticBound = isBuy ? advancedPrice.Low : -advancedPrice.High;

        var advancedPriceAdjustor = advancedPriceWeight > 0 ? pessimisticBound : optimisticBound;
        return predicted * (1 - Math.Abs(advancedPriceWeight)) +
               advancedPriceAdjustor * Math.Abs(advancedPriceWeight);
    }

    // Usage is floored to avoid divide-by-zero / a sign flip on net-production segments.
    private const decimal MinHourlyUsageKwh = 0.01m;

    /// <summary>
    /// Hours of battery runway remaining at a given charge: usable charge above the floor divided
    /// by the expected hourly usage. The caller supplies the usage rate so a future time-of-day
    /// estimate can be passed per segment without changing this method.
    /// </summary>
    public static decimal GetHoursToEmpty(decimal chargeKwh, decimal minCapacity, decimal hourlyUsageKwh)
    {
        var usableKwh = chargeKwh - minCapacity;
        var usage = Math.Max(hourlyUsageKwh, MinHourlyUsageKwh);
        return usableKwh / usage;
    }

    /// <summary>
    /// Signed risk weight from runway. Short runway ramps to +PessimismMaxWeight (lean toward the
    /// High price for buys / Low for sells); deep runway ramps to -OptimismMaxWeight; the band in
    /// between is neutral (0 = use the predicted price). Optimism and pessimism ramp independently.
    /// </summary>
    public static decimal GetRiskWeight(decimal hoursToEmpty, BatteryConfig config)
    {
        if (hoursToEmpty <= config.PessimismStartHours)
        {
            var span = config.PessimismStartHours - config.PessimismMaxAtHours;
            var t = span <= 0 ? 1m : (config.PessimismStartHours - hoursToEmpty) / span;
            return config.PessimismMaxWeight * Math.Clamp(t, 0m, 1m);
        }
        if (hoursToEmpty >= config.OptimismStartHours)
        {
            var span = config.OptimismMaxAtHours - config.OptimismStartHours;
            var t = span <= 0 ? 1m : (hoursToEmpty - config.OptimismStartHours) / span;
            return -config.OptimismMaxWeight * Math.Clamp(t, 0m, 1m);
        }
        return 0m;
    }
}