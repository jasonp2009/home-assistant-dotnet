using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Models;
using src.apps.HassModel.Battery.Usage;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery.Usage;

/// <summary>
/// Unit tests for the pure consumption maths: computing per-interval consumption from cumulative
/// counters (with daily-reset handling), spreading a solar-aligned window across its 5-minute buckets,
/// and the per-time-of-day weighted estimate.
/// </summary>
public class UsageMathTests
{
    private static readonly DateTime T0 = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private static BatteryConfig UsageCfg(decimal multiplier = 1m, int windowCap = 4, decimal maxSegmentKwh = 5m)
    {
        var cfg = Cfg();
        cfg.EstimatedUsageMultiplier = multiplier;
        cfg.UsageMaxWindowSegments = windowCap;
        cfg.UsageMaxSegmentKwh = maxSegmentKwh;
        cfg.UsageWindow1Days = 1; cfg.UsageWindow1Weight = 0.4m;
        cfg.UsageWindow2Days = 3; cfg.UsageWindow2Weight = 0.3m;
        cfg.UsageWindow3Days = 7; cfg.UsageWindow3Weight = 0.3m;
        return cfg;
    }

    private static CounterReading Reading(DateTime t, decimal gridIn = 0, decimal gridOut = 0,
        decimal solar = 0, decimal charge = 0, decimal discharge = 0)
        => new(t, gridIn, gridOut, solar, charge, discharge);

    // ---- ComputeConsumption ------------------------------------------------------------------

    [Fact]
    public void ComputeConsumption_NetsAllFourTerms()
    {
        var prev = Reading(T0, gridIn: 100m, gridOut: 10m, solar: 1000m, charge: 50m, discharge: 40m);
        var cur = Reading(T0.AddMinutes(5), gridIn: 100.5m, gridOut: 10m, solar: 1000.3m, charge: 50.2m, discharge: 40.1m);

        // 0.5 - 0 + 0.3 - (0.2 - 0.1) = 0.7
        Assert.Equal(0.7m, UsageMath.ComputeConsumption(prev, cur));
    }

    [Fact]
    public void ComputeConsumption_BatteryCounterReset_ReturnsNull()
    {
        var prev = Reading(T0, charge: 30m);
        var cur = Reading(T0.AddMinutes(5), gridIn: 0.5m, charge: 2m); // charge counter reset (went down)

        Assert.Null(UsageMath.ComputeConsumption(prev, cur));
    }

    [Fact]
    public void ComputeConsumption_NegativeFromSkew_FlooredToZero()
    {
        var prev = Reading(T0);
        var cur = Reading(T0.AddMinutes(5), gridIn: 0.1m, charge: 1.0m); // 0.1 - (1.0) = -0.9 -> 0

        Assert.Equal(0m, UsageMath.ComputeConsumption(prev, cur));
    }

    [Fact]
    public void ComputeConsumption_LifetimeCounterBackwards_ReturnsNull()
    {
        var prev = Reading(T0, gridIn: 100m);
        var cur = Reading(T0.AddMinutes(5), gridIn: 99.9m); // a lifetime counter shouldn't go backwards

        Assert.Null(UsageMath.ComputeConsumption(prev, cur));
    }

    // ---- SpreadWindow ------------------------------------------------------------------------

    [Fact]
    public void SpreadWindow_SpreadsConsumptionEvenlyAcrossSegments()
    {
        var start = Reading(T0);
        var end = Reading(T0.AddMinutes(15), gridIn: 0.9m); // 15 min = 3 segments, 0.9 kWh total

        var samples = UsageMath.SpreadWindow(start, end, UsageCfg()).ToList();

        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.Equal(0.3m, s.ConsumptionKwh));
        Assert.Equal(new[] { T0, T0.AddMinutes(5), T0.AddMinutes(10) }, samples.Select(s => s.SegmentStartUtc));
    }

    [Fact]
    public void SpreadWindow_PerSegmentOverCap_Discarded()
    {
        var start = Reading(T0);
        var end = Reading(T0.AddMinutes(15), gridIn: 0.9m); // 0.3/segment

        Assert.Empty(UsageMath.SpreadWindow(start, end, UsageCfg(maxSegmentKwh: 0.2m)));
    }

    [Fact]
    public void SpreadWindow_WindowLongerThanCap_Discarded()
    {
        var start = Reading(T0);
        var end = Reading(T0.AddMinutes(30), gridIn: 0.6m); // 6 segments > cap (4)

        Assert.Empty(UsageMath.SpreadWindow(start, end, UsageCfg(windowCap: 4)));
    }

    // ---- BuildSamplesFromReadings (batch backfill) -------------------------------------------

    [Fact]
    public void BuildSamplesFromReadings_SolarJump_SpreadsEvenly_NoSawtooth()
    {
        // Solar only ticks at the boundary after its 15-min period; grid rises smoothly.
        var readings = new List<CounterReading>
        {
            Reading(T0,                 gridIn: 0m,   solar: 0m),
            Reading(T0.AddMinutes(5),   gridIn: 0.1m, solar: 0m),
            Reading(T0.AddMinutes(10),  gridIn: 0.2m, solar: 0m),
            Reading(T0.AddMinutes(15),  gridIn: 0.3m, solar: 0.9m) // solar lump lands here
        };

        var samples = UsageMath.BuildSamplesFromReadings(readings, UsageCfg());

        // Window closes on the solar tick: (0.3 grid + 0.9 solar) = 1.2 over 3 segments = 0.4 each,
        // i.e. the 0.9 solar lump is spread, not dumped into the last segment.
        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.Equal(0.4m, s.ConsumptionKwh));
    }

    [Fact]
    public void BuildSamplesFromReadings_NightWindow_ClosesAtCap_StillEmits()
    {
        // No solar all night; window must close at the cap (4 segments) and still emit load samples.
        var readings = new List<CounterReading>
        {
            Reading(T0,                gridIn: 0m,   solar: 0m),
            Reading(T0.AddMinutes(5),  gridIn: 0.1m, solar: 0m),
            Reading(T0.AddMinutes(10), gridIn: 0.2m, solar: 0m),
            Reading(T0.AddMinutes(15), gridIn: 0.3m, solar: 0m),
            Reading(T0.AddMinutes(20), gridIn: 0.4m, solar: 0m)
        };

        var samples = UsageMath.BuildSamplesFromReadings(readings, UsageCfg(windowCap: 4));

        Assert.Equal(4, samples.Count); // 0.4 over 4 segments
        Assert.All(samples, s => Assert.Equal(0.1m, s.ConsumptionKwh));
    }

    [Fact]
    public void BuildSamplesFromReadings_WindowWithReset_Dropped()
    {
        var readings = new List<CounterReading>
        {
            Reading(T0,                gridIn: 0m,   solar: 0m, charge: 30m),
            Reading(T0.AddMinutes(5),  gridIn: 0.1m, solar: 0m, charge: 30m),
            Reading(T0.AddMinutes(15), gridIn: 0.3m, solar: 0.9m, charge: 2m) // reset inside the window
        };

        Assert.Empty(UsageMath.BuildSamplesFromReadings(readings, UsageCfg()));
    }

    // ---- RebaseResets (daily counter reset) --------------------------------------------------

    [Fact]
    public void RebaseResets_CarriesBatteryCountersAcrossDailyReset()
    {
        var readings = new List<CounterReading>
        {
            Reading(T0,                discharge: 26.0m, charge: 5.0m),
            Reading(T0.AddMinutes(5),  discharge: 26.2m, charge: 5.0m),
            Reading(T0.AddMinutes(10), discharge: 0.1m,  charge: 0.0m), // reset to ~0
            Reading(T0.AddMinutes(15), discharge: 0.3m,  charge: 0.0m)
        };

        var rebased = UsageMath.RebaseResets(readings);

        // Pre-reset total (26.2 / 5.0) is carried forward, so both counters stay monotonic.
        Assert.Equal(new[] { 26.0m, 26.2m, 26.3m, 26.5m }, rebased.Select(r => r.BatteryDischargeKwh));
        Assert.Equal(new[] { 5.0m, 5.0m, 5.0m, 5.0m }, rebased.Select(r => r.BatteryChargeKwh));
    }

    [Fact]
    public void RebaseResets_RecoversTheWindowStraddlingTheReset()
    {
        // A night (no solar) where the battery counters reset mid-window; consumption = Δdischarge.
        var readings = new List<CounterReading>
        {
            Reading(T0,                discharge: 26.0m),
            Reading(T0.AddMinutes(5),  discharge: 26.2m),
            Reading(T0.AddMinutes(10), discharge: 26.4m),
            Reading(T0.AddMinutes(15), discharge: 26.6m),
            Reading(T0.AddMinutes(20), discharge: 0.1m) // reset -> the capped window closes here
        };

        // Raw: the capped window [T0, T0+20] straddles the reset -> negative delta -> dropped.
        Assert.Empty(UsageMath.BuildSamplesFromReadings(readings, UsageCfg(windowCap: 4)));

        // Rebased: the window computes its true delta (26.7-26.0 = 0.7 over 4 segments) and is kept.
        var samples = UsageMath.BuildSamplesFromReadings(UsageMath.RebaseResets(readings), UsageCfg(windowCap: 4));
        Assert.Equal(4, samples.Count);
        Assert.All(samples, s => Assert.Equal(0.175m, s.ConsumptionKwh));
    }

    // ---- EstimateSegmentUsage ----------------------------------------------------------------

    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tod = new(2026, 6, 15, 6, 0, 0, DateTimeKind.Utc); // a 06:00 segment

    [Fact]
    public void EstimateSegmentUsage_BlendsNestedWindowsByWeight()
    {
        var samples = new List<UsageSample>
        {
            new(Tod,                  1.0m), // today        -> in 1d, 3d, 7d
            new(Tod.AddDays(-2),      2.0m), // 2 days ago   -> in 3d, 7d
            new(Tod.AddDays(-5),      3.0m)  // 5 days ago   -> in 7d only
        };

        // avg1=1.0, avg3=(1+2)/2=1.5, avg7=(1+2+3)/3=2.0
        // 1.0*0.4 + 1.5*0.3 + 2.0*0.3 = 1.45 (weights sum to 1)
        var estimate = UsageMath.EstimateSegmentUsage(samples, Tod, Now, UsageCfg(), fallbackKwh: 99m);

        Assert.Equal(1.45m, estimate, precision: 6);
    }

    [Fact]
    public void EstimateSegmentUsage_AppliesMultiplier()
    {
        var samples = new List<UsageSample> { new(Tod, 1.0m), new(Tod.AddDays(-2), 2.0m), new(Tod.AddDays(-5), 3.0m) };

        var estimate = UsageMath.EstimateSegmentUsage(samples, Tod, Now, UsageCfg(multiplier: 1.1m), fallbackKwh: 99m);

        Assert.Equal(1.45m * 1.1m, estimate, precision: 6);
    }

    [Fact]
    public void EstimateSegmentUsage_RenormalisesWhenOnlySomeWindowsHaveData()
    {
        // Only a 5-day-old sample: only the 7-day window matches; its weight (0.3) is renormalised to 1.
        var samples = new List<UsageSample> { new(Tod.AddDays(-5), 5.0m) };

        var estimate = UsageMath.EstimateSegmentUsage(samples, Tod, Now, UsageCfg(), fallbackKwh: 99m);

        Assert.Equal(5.0m, estimate, precision: 6);
    }

    [Fact]
    public void EstimateSegmentUsage_NoMatchingBucket_ReturnsFallback()
    {
        var samples = new List<UsageSample> { new(Tod, 1.0m) }; // 06:00 bucket

        // Target is a 09:00 segment -> different time-of-day bucket -> no data -> fallback.
        var estimate = UsageMath.EstimateSegmentUsage(samples, Tod.AddHours(3), Now, UsageCfg(), fallbackKwh: 42m);

        Assert.Equal(42m, estimate);
    }

    // ---- ComputeRecencyScale -----------------------------------------------------------------

    private static BatteryConfig RecencyCfg(
        bool enabled = true, decimal minScale = 0.7m, decimal maxScale = 2m, decimal downwardGain = 0.5m,
        int minSamples = 1, int baselineLagHours = 24, int baselineDays = 7,
        (int Hours, decimal Weight)[]? windows = null)
    {
        var cfg = UsageCfg();
        cfg.UsageRecencyEnabled = enabled;
        cfg.UsageRecencyMinScale = minScale;
        cfg.UsageRecencyMaxScale = maxScale;
        cfg.UsageRecencyDownwardGain = downwardGain;
        cfg.UsageRecencyMinSamples = minSamples;
        cfg.UsageRecencyBaselineLagHours = baselineLagHours;
        cfg.UsageRecencyBaselineDays = baselineDays;
        windows ??= [(1, 0.35m), (3, 0.25m), (6, 0.2m), (12, 0.12m), (24, 0.08m)];
        var w = new (int, decimal)[5];
        for (var i = 0; i < 5; i++) w[i] = i < windows.Length ? windows[i] : (0, 0m);
        (cfg.UsageRecencyWindow1Hours, cfg.UsageRecencyWindow1Weight) = w[0];
        (cfg.UsageRecencyWindow2Hours, cfg.UsageRecencyWindow2Weight) = w[1];
        (cfg.UsageRecencyWindow3Hours, cfg.UsageRecencyWindow3Weight) = w[2];
        (cfg.UsageRecencyWindow4Hours, cfg.UsageRecencyWindow4Weight) = w[3];
        (cfg.UsageRecencyWindow5Hours, cfg.UsageRecencyWindow5Weight) = w[4];
        return cfg;
    }

    // Recency now = 12:00 UTC (reuses `Now`). Builds a set of 5-minute samples over the last `hours`
    // hours (the "recent" actuals) plus one prior-day sample per matching time-of-day for each of the
    // preceding `baselineDays` days (the "normal" baseline, older than the 24h lag). `recentKwh` and
    // `baselineKwh` set the per-segment values so the ratio is recentKwh/baselineKwh.
    private static List<UsageSample> RecencySamples(decimal recentKwh, decimal baselineKwh, int hours = 24, int baselineDays = 7)
    {
        var samples = new List<UsageSample>();
        var segs = hours * 12; // 5-min segments in the trailing window
        for (var i = 1; i <= segs; i++)
        {
            var t = Now.AddMinutes(-5 * i); // within [now - hours, now)
            samples.Add(new UsageSample(t, recentKwh));
            for (var d = 1; d <= baselineDays; d++)
                samples.Add(new UsageSample(t.AddDays(-d), baselineKwh)); // same time-of-day, prior days
        }
        return samples;
    }

    [Fact]
    public void ComputeRecencyScale_Disabled_ReturnsOne()
    {
        var samples = RecencySamples(recentKwh: 2m, baselineKwh: 1m);
        Assert.Equal(1m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(enabled: false)));
    }

    [Fact]
    public void ComputeRecencyScale_NormalUsage_ReturnsOne()
    {
        var samples = RecencySamples(recentKwh: 1m, baselineKwh: 1m);
        Assert.Equal(1m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg()), precision: 6);
    }

    [Fact]
    public void ComputeRecencyScale_ElevatedRecentUsage_ScalesUpByRatio()
    {
        // Every horizon sees the same 1.5x ratio -> blend is 1.5 -> above-normal applied in full.
        var samples = RecencySamples(recentKwh: 1.5m, baselineKwh: 1m);
        Assert.Equal(1.5m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg()), precision: 6);
    }

    [Fact]
    public void ComputeRecencyScale_AboveMax_ClampedToMaxScale()
    {
        var samples = RecencySamples(recentKwh: 3m, baselineKwh: 1m); // ratio 3 -> clamp to 2
        Assert.Equal(2m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(maxScale: 2m)));
    }

    [Fact]
    public void ComputeRecencyScale_LowUsage_DampedByDownwardGain()
    {
        // ratio 0.5 -> deviation -0.5, damped by gain 0.5 -> -0.25 -> scale 0.75 (above the 0.7 floor).
        var samples = RecencySamples(recentKwh: 0.5m, baselineKwh: 1m);
        Assert.Equal(0.75m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(downwardGain: 0.5m)), precision: 6);
    }

    [Fact]
    public void ComputeRecencyScale_LowUsage_ClampedToMinScale()
    {
        // ratio 0 -> deviation -1, damped -0.5 -> scale 0.5, clamped up to the 0.7 floor.
        var samples = RecencySamples(recentKwh: 0m, baselineKwh: 1m);
        Assert.Equal(0.7m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(minScale: 0.7m, downwardGain: 0.5m)));
    }

    [Fact]
    public void ComputeRecencyScale_SymmetricGain_AppliesDownwardInFull()
    {
        // gain 1 -> ratio 0.5 gives scale 0.5 (would clamp at floor 0.4 here, so keep floor lower).
        var samples = RecencySamples(recentKwh: 0.5m, baselineKwh: 1m);
        Assert.Equal(0.5m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(minScale: 0.4m, downwardGain: 1m)), precision: 6);
    }

    [Fact]
    public void ComputeRecencyScale_InsufficientCoverage_ReturnsOne()
    {
        // Plenty of hot recent samples, but MinSamples set above the coverage of every horizon.
        var samples = RecencySamples(recentKwh: 2m, baselineKwh: 1m, hours: 1); // ~12 recent segments
        Assert.Equal(1m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(minSamples: 1000)));
    }

    [Fact]
    public void ComputeRecencyScale_NoBaseline_ReturnsOne()
    {
        // Recent samples only (no prior-day baseline) -> every recent sample is uncovered -> neutral.
        var samples = RecencySamples(recentKwh: 2m, baselineKwh: 1m, baselineDays: 0);
        Assert.Equal(1m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg()));
    }

    [Fact]
    public void ComputeRecencyScale_Gradient_WeightsRecentHoursMoreHeavily()
    {
        // Hot only in the last hour (2x), normal before it. A recent-heavy gradient must yield a
        // higher scale than a far-heavy one, proving the horizon weighting biases toward recent hours.
        var samples = new List<UsageSample>();
        for (var i = 1; i <= 24 * 12; i++)
        {
            var t = Now.AddMinutes(-5 * i);
            var recent = t >= Now.AddHours(-1) ? 2m : 1m; // last hour hot, rest normal
            samples.Add(new UsageSample(t, recent));
            for (var d = 1; d <= 7; d++)
                samples.Add(new UsageSample(t.AddDays(-d), 1m));
        }

        var recentHeavy = UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(
            windows: [(1, 1m), (3, 0m), (6, 0m), (12, 0m), (24, 0m)]));
        var farHeavy = UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(
            windows: [(1, 0m), (3, 0m), (6, 0m), (12, 0m), (24, 1m)]));

        Assert.True(recentHeavy > farHeavy, $"recent-heavy {recentHeavy} should exceed far-heavy {farHeavy}");
        Assert.Equal(2m, recentHeavy, precision: 6); // 1h horizon is entirely the 2x hour
    }
}
