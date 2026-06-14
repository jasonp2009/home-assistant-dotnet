using System.Collections.Generic;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Mechanical unit tests for the planner's building blocks (segment projection and boundary
/// detection). These document current behaviour and should pass.
/// </summary>
public class BatteryPlannerTests
{
    [Fact]
    public void CalculateBoundaryResult_DetectsMinCrossing()
    {
        var segs = new List<EnergySegment> { Seg(0, 10), Seg(1, 9), Seg(2, 8), Seg(3, 7) };

        var result = BatteryPlanner.CalculateBoundaryResult(segs, Cfg());

        Assert.True(result.IsOutOfBounds);
        Assert.False(result.IsMax);
        Assert.Equal(3, result.IndexOfBoundaryCrossing);
    }

    [Fact]
    public void CalculateBoundaryResult_DetectsMaxCrossing()
    {
        var segs = new List<EnergySegment> { Seg(0, 48), Seg(1, 49), Seg(2, 50), Seg(3, 51) };

        var result = BatteryPlanner.CalculateBoundaryResult(segs, Cfg());

        Assert.True(result.IsOutOfBounds);
        Assert.True(result.IsMax);
        Assert.Equal(3, result.IndexOfBoundaryCrossing);
    }

    [Fact]
    public void CalculateBoundaryResult_WithinBounds_ReturnsNoCrossing()
    {
        var segs = new List<EnergySegment> { Seg(0, 30), Seg(1, 29), Seg(2, 28) };

        var result = BatteryPlanner.CalculateBoundaryResult(segs, Cfg());

        Assert.False(result.IsOutOfBounds);
        Assert.Null(result.IsMax);
        Assert.Null(result.IndexOfBoundaryCrossing);
    }

    [Fact]
    public void GetBatteryUntil_ReturnsStartOfFirstSegmentBelowMin()
    {
        var segs = new List<EnergySegment> { Seg(0, 10), Seg(1, 9), Seg(2, 7), Seg(3, 6) };

        var until = BatteryPlanner.GetBatteryUntil(segs, Cfg());

        Assert.Equal(Base.AddMinutes(10), until); // index 2 is the first below MinCapacity (8)
    }

    [Fact]
    public void GetBatteryUntil_NeverBelowMin_ReturnsMaxValue()
    {
        var segs = new List<EnergySegment> { Seg(0, 30), Seg(1, 25), Seg(2, 20) };

        Assert.Equal(System.DateTime.MaxValue, BatteryPlanner.GetBatteryUntil(segs, Cfg()));
    }

    [Fact]
    public void BuildSegments_DepletesByUsageEachSegment()
    {
        var prices = new List<BaseInterval> { GeneralInterval(0, 20m), GeneralInterval(1, 20m), GeneralInterval(2, 20m) };

        var segs = BatteryPlanner.BuildSegments(Base, currentChargeKwh: 30m, averageSegmentUsage: 0.5m, solarForecast: null, prices, Cfg());

        Assert.Equal(30m, segs[0].EstimatedBatteryChargeKwh);
        Assert.Equal(29.5m, segs[1].EstimatedBatteryChargeKwh);
        Assert.Equal(29.0m, segs[2].EstimatedBatteryChargeKwh);
    }

    [Fact]
    public void BuildSegments_SpreadsSolarForecastPeriodAcrossItsSegments()
    {
        var prices = new List<BaseInterval>
        {
            GeneralInterval(0, 20m), GeneralInterval(1, 20m), GeneralInterval(2, 20m), GeneralInterval(3, 20m)
        };
        // A 900 Wh 15-minute period at 00:00; the 00:15 point (0 Wh) just defines the period length.
        // The period spans the three 5-min segments [00:00,00:15), so each takes 0.3 kWh, not 0.9 dumped
        // into one segment.
        var solar = new Dictionary<System.DateTime, int> { [Base] = 900, [Base.AddMinutes(15)] = 0 };

        var segs = BatteryPlanner.BuildSegments(Base, currentChargeKwh: 20m, averageSegmentUsage: 0m, solar, prices, Cfg());

        Assert.Equal(0.3m, segs[0].SolarForecastKwh, precision: 3);
        Assert.Equal(0.3m, segs[1].SolarForecastKwh, precision: 3);
        Assert.Equal(0.3m, segs[2].SolarForecastKwh, precision: 3);
        Assert.Equal(0m, segs[3].SolarForecastKwh); // next period carries 0 Wh
        Assert.Equal(20.9m, segs[2].EstimatedBatteryChargeKwh, precision: 3); // 20 + 3 * 0.3, no usage
    }
}
