using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery.Models;

namespace src.apps.HassModel.Battery.Usage;

/// <summary>
/// Pure consumption math (no Home Assistant / IO), so it can be unit tested. Turns cumulative counter
/// readings into per-5-minute <see cref="UsageSample"/>s and estimates per-time-of-day usage from them.
/// The solar lifetime counter only advances every ~15 min, so consumption is always measured over a
/// solar-aligned window and spread evenly across the segments it covers (mirroring
/// <c>ApplySolarForecast</c>), never differenced over a single 5-min slot.
/// </summary>
public static class UsageMath
{
    /// <summary>
    /// Household consumption (kWh) over <c>[prev, cur]</c> from the cumulative counters:
    /// <c>ΔgridIn − ΔgridOut + Δsolar − (Δcharge − Δdischarge)</c>. Returns null when a daily reset of
    /// the battery charge/discharge counters is detected (a backwards delta), or when a lifetime
    /// counter goes backwards (sensor glitch). Tiny negative results from sensor update skew floor to 0.
    /// </summary>
    public static decimal? ComputeConsumption(CounterReading prev, CounterReading cur)
    {
        var dCharge = cur.BatteryChargeKwh - prev.BatteryChargeKwh;
        var dDischarge = cur.BatteryDischargeKwh - prev.BatteryDischargeKwh;
        if (dCharge < 0 || dDischarge < 0) return null; // daily counter reset inside the interval

        var dGridIn = cur.GridInKwh - prev.GridInKwh;
        var dGridOut = cur.GridOutKwh - prev.GridOutKwh;
        var dSolar = cur.SolarKwh - prev.SolarKwh;
        if (dGridIn < 0 || dGridOut < 0 || dSolar < 0) return null; // lifetime counter went backwards

        var consumption = dGridIn - dGridOut + dSolar - (dCharge - dDischarge);
        return consumption < 0 ? 0m : consumption;
    }

    /// <summary>
    /// Spreads the consumption measured over a window <c>[windowStart, cur]</c> evenly across the
    /// 5-minute segments it covers (the window is solar-aligned by the caller). Yields nothing when the
    /// window is invalid: non-positive/over-cap length, a counter reset inside it
    /// (<see cref="ComputeConsumption"/> returns null), or an implausibly large per-segment value
    /// (&gt; <c>UsageMaxSegmentKwh</c>).
    /// </summary>
    public static IEnumerable<UsageSample> SpreadWindow(CounterReading windowStart, CounterReading cur, BatteryConfig config)
    {
        var segment = config.SegmentSize;
        var k = SegmentCount(windowStart.TimestampUtc, cur.TimestampUtc, segment);
        if (k < 1 || k > config.UsageMaxWindowSegments) yield break;

        var consumption = ComputeConsumption(windowStart, cur);
        if (consumption is null) yield break;

        var perSegment = consumption.Value / k;
        if (perSegment > config.UsageMaxSegmentKwh) yield break;

        for (var j = 0; j < k; j++)
            yield return new UsageSample(windowStart.TimestampUtc + j * segment, perSegment);
    }

    /// <summary>
    /// Batch (backfill) analogue of the live windowing: walks 5-minute boundary readings, opening a
    /// window at the last emitted reading and closing it when the solar counter advances (a complete
    /// 15-min increment) or the window reaches <c>UsageMaxWindowSegments</c>. Each closed window is
    /// spread by <see cref="SpreadWindow"/>.
    /// </summary>
    public static List<UsageSample> BuildSamplesFromReadings(IReadOnlyList<CounterReading> readings, BatteryConfig config)
    {
        var samples = new List<UsageSample>();
        if (readings.Count < 2) return samples;

        var anchor = readings[0];
        for (var i = 1; i < readings.Count; i++)
        {
            var cur = readings[i];
            var k = SegmentCount(anchor.TimestampUtc, cur.TimestampUtc, config.SegmentSize);
            var solarAdvanced = cur.SolarKwh > anchor.SolarKwh;
            if (!solarAdvanced && k < config.UsageMaxWindowSegments) continue; // keep the window open
            samples.AddRange(SpreadWindow(anchor, cur, config));
            anchor = cur;
        }
        return samples;
    }

    /// <summary>
    /// Per-time-of-day usage estimate (kWh per segment) for the segment starting at
    /// <paramref name="targetSegmentStartUtc"/>. Averages samples sharing the target's local
    /// time-of-day bucket over each configured window (e.g. last 1/3/7 days), blends them by the
    /// configured weights (renormalised over windows that actually have data), and applies
    /// <c>EstimatedUsageMultiplier</c>. Falls back to <paramref name="fallbackKwh"/> (already
    /// multiplier-adjusted by the caller) when no window has a matching sample.
    /// </summary>
    public static decimal EstimateSegmentUsage(
        IReadOnlyCollection<UsageSample> samples,
        DateTime targetSegmentStartUtc,
        DateTime nowUtc,
        BatteryConfig config,
        decimal fallbackKwh)
    {
        var targetKey = LocalTimeOfDayKey(targetSegmentStartUtc, config.SegmentSizeMins);
        var matching = samples
            .Where(s => LocalTimeOfDayKey(s.SegmentStartUtc, config.SegmentSizeMins) == targetKey)
            .ToList();
        if (matching.Count == 0) return fallbackKwh;

        decimal weightedSum = 0m;
        decimal weightUsed = 0m;
        foreach (var (days, weight) in config.UsageEstimateWindows)
        {
            if (weight <= 0m || days <= 0) continue;
            var cutoff = nowUtc - TimeSpan.FromDays(days);
            var window = matching.Where(s => s.SegmentStartUtc >= cutoff).ToList();
            if (window.Count == 0) continue;
            weightedSum += window.Average(s => s.ConsumptionKwh) * weight;
            weightUsed += weight;
        }
        if (weightUsed <= 0m) return fallbackKwh;
        return weightedSum / weightUsed * config.EstimatedUsageMultiplier;
    }

    /// <summary>The UTC start of the segment containing <paramref name="utc"/>, aligned to <paramref name="segment"/>.</summary>
    public static DateTime SegmentStart(DateTime utc, TimeSpan segment)
        => utc - TimeSpan.FromTicks(utc.Ticks % segment.Ticks);

    private static int SegmentCount(DateTime startUtc, DateTime endUtc, TimeSpan segment)
        => (int)Math.Round((endUtc - startUtc) / segment, MidpointRounding.AwayFromZero);

    private static int LocalTimeOfDayKey(DateTime utc, int segmentSizeMins)
    {
        var asUtc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = asUtc.ToLocalTime();
        return (local.Hour * 60 + local.Minute) / segmentSizeMins;
    }
}
