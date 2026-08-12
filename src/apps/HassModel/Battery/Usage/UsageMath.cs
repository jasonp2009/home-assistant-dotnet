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
    /// Returns a copy of <paramref name="readings"/> (time-ordered) with the battery charge/discharge
    /// counters made monotonic across their daily reset: when a counter reads lower than the previous
    /// reading (a reset), its pre-reset total is carried forward as an offset. The lifetime grid/solar
    /// counters are left untouched. Without this, the once-a-day reset makes the window straddling
    /// midnight compute a large negative battery delta, so it is dropped — leaving the midnight
    /// time-of-day buckets empty and the estimate falling back to the (higher) flat average there.
    /// </summary>
    public static List<CounterReading> RebaseResets(IReadOnlyList<CounterReading> readings)
    {
        var result = new List<CounterReading>(readings.Count);
        decimal chargeOffset = 0m, dischargeOffset = 0m, prevCharge = 0m, prevDischarge = 0m;
        for (var i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            if (i > 0)
            {
                if (r.BatteryChargeKwh < prevCharge) chargeOffset += prevCharge;
                if (r.BatteryDischargeKwh < prevDischarge) dischargeOffset += prevDischarge;
            }
            prevCharge = r.BatteryChargeKwh;
            prevDischarge = r.BatteryDischargeKwh;
            result.Add(r with
            {
                BatteryChargeKwh = r.BatteryChargeKwh + chargeOffset,
                BatteryDischargeKwh = r.BatteryDischargeKwh + dischargeOffset
            });
        }
        return result;
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

    /// <summary>The trailing span treated as "recent" — i.e. the current day's run of samples, compared
    /// against the same clock-times on PRIOR days. Also the baseline's exclusion window: a sample can't
    /// be part of the norm it is being judged against, or a hot spell would inflate its own reference.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    /// <summary>Minimum recent samples with a known norm before the scale is trusted (else neutral).</summary>
    private const int MinRecentSamples = 6;

    /// <summary>
    /// Recency scale: a multiplier on the forward usage estimate that reacts to recent MEASURED
    /// consumption running above (or below) the learned time-of-day norm — so a day running hotter
    /// than usual pulls the projection tighter WITHIN the day, instead of only a day later once those
    /// samples age into the time-of-day windows of <see cref="EstimateSegmentUsage"/>.
    ///
    /// Each sample in the last <see cref="RecentWindow"/> is compared against the mean of its own
    /// time-of-day bucket on earlier days, and weighted by an exponential decay on its age
    /// (<c>UsageRecencyHalfLifeHours</c>) — so the last hour dominates, the previous few hours count
    /// progressively less, and a day-old sample barely registers. The resulting deviation is applied
    /// asymmetrically: ABOVE-normal in full, BELOW-normal damped by <c>UsageRecencyDownwardGain</c>
    /// (reacting faster to high usage than low), then clamped to
    /// [<c>UsageRecencyMinScale</c>, <c>UsageRecencyMaxScale</c>]. Returns a neutral 1 when disabled or
    /// when too few recent samples have a known norm.
    /// </summary>
    public static decimal ComputeRecencyScale(
        IReadOnlyCollection<UsageSample> samples,
        DateTime nowUtc,
        BatteryConfig config)
    {
        if (!config.UsageRecencyEnabled || config.UsageRecencyHalfLifeHours <= 0m) return 1m;
        var recentStart = nowUtc - RecentWindow;

        // The norm per time-of-day bucket, from samples older than the recent window. Built in one pass
        // and reused, so the whole scale costs a couple of passes over the sample set.
        var norms = new Dictionary<int, (decimal Sum, int Count)>();
        foreach (var s in samples)
        {
            if (s.SegmentStartUtc >= recentStart) continue;
            var key = LocalTimeOfDayKey(s.SegmentStartUtc, config.SegmentSizeMins);
            var cur = norms.TryGetValue(key, out var v) ? v : default;
            norms[key] = (cur.Sum + s.ConsumptionKwh, cur.Count + 1);
        }

        decimal actual = 0m, expected = 0m;
        var covered = 0;
        foreach (var s in samples)
        {
            if (s.SegmentStartUtc < recentStart || s.SegmentStartUtc >= nowUtc) continue;
            if (!norms.TryGetValue(LocalTimeOfDayKey(s.SegmentStartUtc, config.SegmentSizeMins), out var n)) continue;
            var normal = n.Sum / n.Count;
            if (normal <= 0m) continue;

            var ageHours = Convert.ToDecimal((nowUtc - s.SegmentStartUtc).TotalHours);
            var weight = DecayWeight(ageHours, config.UsageRecencyHalfLifeHours);
            actual += weight * s.ConsumptionKwh;
            expected += weight * normal;
            covered++;
        }
        if (covered < MinRecentSamples || expected <= 0m) return 1m;

        var deviation = actual / expected - 1m;                   // >0 hot, <0 cool, relative to normal
        if (deviation < 0m) deviation *= config.UsageRecencyDownwardGain; // damp the below-normal side
        return Math.Clamp(1m + deviation, config.UsageRecencyMinScale, config.UsageRecencyMaxScale);
    }

    /// <summary>Exponential age decay: weight <c>0.5</c> at one half-life, <c>0.25</c> at two, and so on.</summary>
    private static decimal DecayWeight(decimal ageHours, decimal halfLifeHours)
        => Convert.ToDecimal(Math.Pow(0.5, Convert.ToDouble(ageHours / halfLifeHours)));

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
