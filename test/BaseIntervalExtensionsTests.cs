using System.Collections.Generic;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Clients.AmberClient.Extensions;
using Xunit;

namespace Tests;

/// <summary>
/// Characterisation tests for the Amber interval price helpers. These document the
/// existing (unchanged) behaviour so the pricing pipeline is protected during the
/// weighted-price changes.
/// </summary>
public class BaseIntervalExtensionsTests
{
    [Fact]
    public void GetPrice_ReturnsAdvancedPredicted_WhenAdvancedPresent()
    {
        var interval = new CurrentInterval
        {
            PerKwh = 25m,
            AdvancedPrice = new AdvancedPrice { Low = 10m, Predicted = 18m, High = 30m }
        };

        Assert.Equal(18m, interval.GetPrice());
    }

    [Fact]
    public void GetPrice_FallsBackToPerKwh_WhenNoAdvanced()
    {
        var interval = new ForecastInterval { PerKwh = 22m, AdvancedPrice = null };

        Assert.Equal(22m, interval.GetPrice());
    }

    [Fact]
    public void GetAdvancedPrice_ReturnsValue_ForForecastInterval()
    {
        var advanced = new AdvancedPrice { Predicted = 5m };
        var interval = new ForecastInterval { AdvancedPrice = advanced };

        Assert.Same(advanced, interval.GetAdvancedPrice());
    }

    [Fact]
    public void GetAdvancedPrice_ReturnsNull_ForActualInterval()
    {
        // Note: GetAdvancedPrice only unwraps Current/Forecast intervals, so an
        // ActualInterval's advanced price is intentionally ignored.
        var interval = new ActualInterval { AdvancedPrice = new AdvancedPrice { Predicted = 5m } };

        Assert.Null(interval.GetAdvancedPrice());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsEstimate_CurrentInterval_ReflectsEstimateFlag(bool estimate, bool expected)
    {
        var interval = new CurrentInterval { Estimate = estimate };

        Assert.Equal(expected, interval.IsEstimate());
    }

    [Fact]
    public void IsEstimate_ForecastInterval_IsAlwaysTrue()
    {
        Assert.True(new ForecastInterval().IsEstimate());
    }

    [Fact]
    public void IsEstimate_Enumerable_TrueIfAnyCurrentIntervalIsEstimate()
    {
        var intervals = new List<BaseInterval>
        {
            new CurrentInterval { Estimate = false },
            new CurrentInterval { Estimate = true }
        };

        Assert.True(intervals.IsEstimate());
    }

    [Fact]
    public void IsEstimate_Enumerable_FalseWhenNoCurrentIntervalIsEstimate()
    {
        var intervals = new List<BaseInterval>
        {
            new CurrentInterval { Estimate = false },
            new ForecastInterval()
        };

        Assert.False(intervals.IsEstimate());
    }
}
