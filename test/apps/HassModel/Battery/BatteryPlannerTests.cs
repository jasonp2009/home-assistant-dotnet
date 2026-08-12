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

    [Fact]
    public void BuildSegments_DrainsByPerSegmentEstimate()
    {
        var prices = new List<BaseInterval> { GeneralInterval(0, 20m), GeneralInterval(1, 20m), GeneralInterval(2, 20m) };
        // Usage varies by time of day: 0.5 kWh for the first slot, 1.0 kWh thereafter. The estimator is
        // queried with the start of the segment being drained.
        decimal Usage(System.DateTime start) => start == Base ? 0.5m : 1.0m;

        var segs = BatteryPlanner.BuildSegments(Base, currentChargeKwh: 30m, Usage, solarForecast: null, prices, Cfg());

        Assert.Equal(30m, segs[0].EstimatedBatteryChargeKwh);
        Assert.Equal(29.5m, segs[1].EstimatedBatteryChargeKwh); // drained by Usage(Base) = 0.5
        Assert.Equal(28.5m, segs[2].EstimatedBatteryChargeKwh); // drained by Usage(Base+5min) = 1.0
    }

    [Fact]
    public void BuildSegments_ScalesUsageForDemandWindowSegments()
    {
        var cfg = Cfg();
        cfg.EstimatedUsageMultiplier = 1m;
        cfg.DemandWindowUsageMultiplier = 1.5m;
        // Segment 1 is a demand window; segments 0 and 2 are not. Flat 0.5 kWh estimate everywhere.
        var prices = new List<BaseInterval>
        {
            GeneralInterval(0, 20m), GeneralInterval(1, 20m, demand: true), GeneralInterval(2, 20m)
        };

        var segs = BatteryPlanner.BuildSegments(Base, currentChargeKwh: 30m, averageSegmentUsage: 0.5m, solarForecast: null, prices, cfg);

        Assert.False(segs[0].IsDemandWindow);
        Assert.True(segs[1].IsDemandWindow);
        Assert.Equal(0.5m, segs[0].UsageKwh);   // normal segment: unchanged
        Assert.Equal(0.75m, segs[1].UsageKwh);  // demand window: 0.5 * 1.5
        // Charge trajectory reflects the inflated demand-window drain.
        Assert.Equal(30m, segs[0].EstimatedBatteryChargeKwh);
        Assert.Equal(29.5m, segs[1].EstimatedBatteryChargeKwh);   // -0.5 (seg 0, normal)
        Assert.Equal(28.75m, segs[2].EstimatedBatteryChargeKwh);  // -0.75 (seg 1, demand window)
    }

    [Fact]
    public void BuildSegments_UnsetDemandWindowMultiplier_LeavesUsageUnchanged()
    {
        var cfg = Cfg(); // DemandWindowUsageMultiplier defaults to 0 (feature off)
        var prices = new List<BaseInterval>
        {
            GeneralInterval(0, 20m), GeneralInterval(1, 20m, demand: true), GeneralInterval(2, 20m)
        };

        var segs = BatteryPlanner.BuildSegments(Base, currentChargeKwh: 30m, averageSegmentUsage: 0.5m, solarForecast: null, prices, cfg);

        Assert.True(segs[1].IsDemandWindow);
        Assert.Equal(0.5m, segs[1].UsageKwh); // no scaling when the multiplier is unset
        Assert.Equal(29m, segs[2].EstimatedBatteryChargeKwh); // -0.5 each, no demand inflation
    }
}
