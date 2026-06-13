using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Extensions;
using Xunit;

namespace Tests;

/// <summary>
/// Unit tests for the runway-based risk weight (GetRiskWeight) and the hours-to-empty calculation.
/// Uses the proposed default ramp configuration.
/// </summary>
public class RiskWeightTests
{
    private static BatteryConfig Config() => new()
    {
        MinCapacity = 8m,
        MaxCapacity = 50m,
        PessimismStartHours = 20m,
        PessimismMaxAtHours = 4m,
        PessimismMaxWeight = 0.7m,
        OptimismStartHours = 26m,
        OptimismMaxAtHours = 32m,
        OptimismMaxWeight = 0.3m
    };

    [Theory]
    [InlineData(20, 0.0)]    // at pessimism start -> neutral edge
    [InlineData(12, 0.35)]   // mid ramp: 0.7 * (20-12)/16
    [InlineData(4, 0.7)]     // fully pessimistic
    [InlineData(0, 0.7)]     // beyond max-at-hours -> clamped
    [InlineData(-5, 0.7)]    // past empty -> clamped
    public void GetRiskWeight_ShortRunway_RampsPessimism(double hours, double expected)
    {
        Assert.Equal((decimal)expected, EnergySegmentExtensions.GetRiskWeight((decimal)hours, Config()), 6);
    }

    [Theory]
    [InlineData(20.001)]
    [InlineData(23)]
    [InlineData(25.999)]
    public void GetRiskWeight_NeutralBand_IsZero(double hours)
    {
        Assert.Equal(0m, EnergySegmentExtensions.GetRiskWeight((decimal)hours, Config()), 6);
    }

    [Theory]
    [InlineData(26, 0.0)]    // at optimism start
    [InlineData(29, -0.15)]  // mid ramp: -0.3 * (29-26)/6
    [InlineData(32, -0.3)]   // fully optimistic
    [InlineData(40, -0.3)]   // beyond -> clamped
    public void GetRiskWeight_DeepRunway_RampsOptimism(double hours, double expected)
    {
        Assert.Equal((decimal)expected, EnergySegmentExtensions.GetRiskWeight((decimal)hours, Config()), 6);
    }

    [Fact]
    public void GetRiskWeight_DefaultsArePessimismHeavier_AndWider()
    {
        var config = Config();
        var maxPessimism = EnergySegmentExtensions.GetRiskWeight(0m, config);
        var maxOptimism = EnergySegmentExtensions.GetRiskWeight(1000m, config);

        Assert.Equal(0.7m, maxPessimism);
        Assert.Equal(-0.3m, maxOptimism);
        // pessimism reaches deeper in magnitude...
        Assert.True(maxPessimism > -maxOptimism);
        // ...and over a wider runway band (start - maxAt) than optimism.
        var pessimismSpan = config.PessimismStartHours - config.PessimismMaxAtHours; // 16
        var optimismSpan = config.OptimismMaxAtHours - config.OptimismStartHours;    // 6
        Assert.True(pessimismSpan > optimismSpan);
    }

    [Theory]
    [InlineData(20, 8, 1.2, 10)]      // (20-8)/1.2
    [InlineData(50, 8, 1.32, 31.8182)] // (50-8)/1.32
    public void GetHoursToEmpty_DividesUsableChargeByUsage(double charge, double min, double usage, double expected)
    {
        Assert.Equal((decimal)expected, EnergySegmentExtensions.GetHoursToEmpty((decimal)charge, (decimal)min, (decimal)usage), 4);
    }

    [Fact]
    public void GetHoursToEmpty_NonPositiveUsage_YieldsLargeRunway_DrivingOptimism()
    {
        var hours = EnergySegmentExtensions.GetHoursToEmpty(20m, 8m, 0m);

        Assert.True(hours > 1000m); // usage floored, no divide-by-zero
        Assert.Equal(-0.3m, EnergySegmentExtensions.GetRiskWeight(hours, Config())); // deep runway -> max optimism
    }

    [Fact]
    public void GetHoursToEmpty_BelowMinCapacity_IsNegative_DrivingMaxPessimism()
    {
        var hours = EnergySegmentExtensions.GetHoursToEmpty(5m, 8m, 1.0m);

        Assert.True(hours < 0m);
        Assert.Equal(0.7m, EnergySegmentExtensions.GetRiskWeight(hours, Config())); // past empty -> max pessimism
    }
}
