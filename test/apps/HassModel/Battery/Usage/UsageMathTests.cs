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
/// the per-time-of-day weighted estimate, and the recency scale applied on top of it.
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
        bool enabled = true, decimal halfLifeHours = 4m, decimal downwardGain = 0.5m)
    {
        var cfg = UsageCfg();
        cfg.UsageRecencyEnabled = enabled;
        cfg.UsageRecencyHalfLifeHours = halfLifeHours;
        cfg.UsageRecencyDownwardGain = downwardGain;
        return cfg;
    }

    /// A day of 5-minute samples ending at <see cref="Now"/>, each `recentMultiplier` times the norm,
    /// with `baselineDays` prior days at the norm itself. `profile` sets the norm per bucket: pass a
    /// varying one to make the per-time-of-day lookup observable, since a flat norm hides it entirely
    /// (a global mean over every older sample would give identical answers).
    private static List<UsageSample> RecencySamples(
        decimal recentMultiplier, int baselineDays = 7, Func<int, decimal>? profile = null)
    {
        profile ??= _ => 1m;
        var samples = new List<UsageSample>();
        for (var i = 1; i <= 24 * 12; i++)
        {
            var t = Now.AddMinutes(-5 * i);
            var norm = profile(i);
            samples.Add(new UsageSample(t, norm * recentMultiplier));
            for (var d = 1; d <= baselineDays; d++)
                samples.Add(new UsageSample(t.AddDays(-d), norm)); // same local bucket, earlier day
        }
        return samples;
    }

    /// A day shape — heavy recent peak, moderate, then light — keyed on how many segments ago a sample
    /// falls rather than on its wall-clock hour. Age-keying matters: a clock-hour shape slides under the
    /// decay curve depending on the machine's timezone, so a mutation that a shaped fixture is meant to
    /// catch could survive on a runner in another zone. Keyed on age, both the correct result and a
    /// global-mean result are the same everywhere.
    private static decimal DailyShape(int segmentsAgo) => segmentsAgo switch
    {
        <= 60 => 0.40m,  // the last 5 h
        <= 132 => 0.15m, // 5-11 h ago
        _ => 0.05m       // 11-24 h ago
    };

    [Theory]
    // recent vs norm -> scale. Above-normal counts in full; below-normal is halved by the 0.5 gain.
    [InlineData(1.0, 1.0)]     // normal
    [InlineData(1.5, 1.5)]     // hot
    [InlineData(3.0, 2.0)]     // deviation +2.0 clamped at MaxScale
    [InlineData(0.5, 0.75)]    // deviation -0.5 damped to -0.25
    [InlineData(0.0, 0.7)]     // deviation -1.0 damped to -0.5, clamped at MinScale
    public void ComputeRecencyScale_MapsRecentUsageAgainstNormToScale(double recentMultiplier, double expected)
    {
        var samples = RecencySamples((decimal)recentMultiplier);

        Assert.Equal((decimal)expected, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg()), precision: 6);
    }

    [Theory]
    [InlineData(1.0, 1.0)] // a normal day against a SHAPED norm must stay neutral
    [InlineData(1.5, 1.5)] // a uniformly hotter day is measured per bucket, not against a daily average
    public void ComputeRecencyScale_ComparesEachSampleWithItsOwnTimeOfDayNorm(double recentMultiplier, double expected)
    {
        // The light part of the day is 8x lighter than the peak, so judging a sample against a single
        // all-day mean instead of its own time-of-day bucket lands nowhere near the expected scale.
        var samples = RecencySamples((decimal)recentMultiplier, profile: DailyShape);

        Assert.Equal((decimal)expected, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg()), precision: 6);
    }

    [Fact]
    public void ComputeRecencyScale_SymmetricGain_AppliesDownwardInFull()
    {
        var samples = RecencySamples(recentMultiplier: 0.8m);

        // gain 1 -> the -0.2 deviation is applied whole, rather than halved to 0.9.
        Assert.Equal(0.8m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(downwardGain: 1m)), precision: 6);
    }

    [Fact]
    public void ComputeRecencyScale_Disabled_ReturnsOne()
    {
        var samples = RecencySamples(recentMultiplier: 2m);
        Assert.Equal(1m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(enabled: false)));
    }

    [Fact]
    public void ComputeRecencyScale_ZeroHalfLife_ReturnsOne()
    {
        var samples = RecencySamples(recentMultiplier: 2m);
        Assert.Equal(1m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(halfLifeHours: 0m)));
    }

    [Fact]
    public void ComputeRecencyScale_TodaysUsageIsExcludedFromItsOwnNorm()
    {
        // A single prior day, so contaminating the norm with today would be maximally visible:
        // it would read (1 + 1.4) / 2 = 1.2 and yield 1.167 instead of 1.4.
        var samples = RecencySamples(recentMultiplier: 1.4m, baselineDays: 1);

        Assert.Equal(1.4m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg()), precision: 6);
    }

    [Fact]
    public void ComputeRecencyScale_WeightHalvesEveryHalfLife()
    {
        // Two groups exactly one half-life apart, so the older weighs half the newer regardless of the
        // shared leading weight: (1.0 + 0.5*2.0) / (1 + 0.5) = 4/3, which sits inside the clamps. Pinning
        // the arithmetic catches 0.5^(age/halfLife) being swapped for e^(-age/halfLife) (which would give
        // ~1.269) — a silent retune of the deployed half-life that an ordering-only assertion misses.
        var newer = Now.AddMinutes(-5);
        var older = newer.AddHours(-2);
        var samples = new List<UsageSample>();
        for (var i = 0; i < 3; i++) // 6 recent samples total: exactly the coverage minimum
        {
            samples.Add(new UsageSample(newer, 1.0m));
            samples.Add(new UsageSample(older, 2.0m));
            for (var d = 1; d <= 3; d++)
            {
                samples.Add(new UsageSample(newer.AddDays(-d), 1m));
                samples.Add(new UsageSample(older.AddDays(-d), 1m));
            }
        }

        var scale = UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(halfLifeHours: 2m));

        Assert.Equal(4m / 3m, scale, precision: 6);
    }

    [Fact]
    public void ComputeRecencyScale_InsufficientCoverage_ReturnsOne()
    {
        // Only 3 recent samples with a known norm — below the minimum for the scale to be trusted.
        var samples = new List<UsageSample>();
        for (var i = 1; i <= 3; i++)
        {
            var t = Now.AddMinutes(-5 * i);
            samples.Add(new UsageSample(t, 2m));
            samples.Add(new UsageSample(t.AddDays(-1), 1m));
        }

        Assert.Equal(1m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg()));
    }

    [Fact]
    public void ComputeRecencyScale_NoBaseline_ReturnsOne()
    {
        // Recent samples only (no prior-day baseline) -> every recent sample is uncovered -> neutral.
        var samples = RecencySamples(recentMultiplier: 2m, baselineDays: 0);
        Assert.Equal(1m, UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg()));
    }

    // Hot only in the last hour (2x normal), ordinary for the 23 h before it.
    private static List<UsageSample> SpikeInLastHourSamples()
    {
        var samples = new List<UsageSample>();
        for (var i = 1; i <= 24 * 12; i++)
        {
            var t = Now.AddMinutes(-5 * i);
            samples.Add(new UsageSample(t, t >= Now.AddHours(-1) ? 2m : 1m));
            for (var d = 1; d <= 7; d++)
                samples.Add(new UsageSample(t.AddDays(-d), 1m)); // norm is 1 at every time-of-day
        }
        return samples;
    }

    [Fact]
    public void ComputeRecencyScale_ShortHalfLife_WeightsRecentHoursMoreHeavily()
    {
        var samples = SpikeInLastHourSamples();

        // A short half-life concentrates the weight on the spike; a long one averages it away over the
        // whole day. This is the "last hour has a higher impact than 3/6/12/24 h" behaviour, as one knob.
        var twitchy = UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(halfLifeHours: 0.5m));
        var smooth = UsageMath.ComputeRecencyScale(samples, Now, RecencyCfg(halfLifeHours: 24m));

        Assert.True(twitchy > smooth, $"short half-life {twitchy} should exceed long half-life {smooth}");
        Assert.True(twitchy > 1.7m, $"short half-life should track the 2x spike closely, got {twitchy}");
        Assert.True(smooth < 1.2m, $"long half-life should dilute the 1-in-24h spike, got {smooth}");
    }
}
