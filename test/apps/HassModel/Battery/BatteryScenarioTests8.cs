using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Demonstrates how hourlyUsage drives runway-based optimism/pessimism:
///   high usage  → short runway  → pessimism inflates uncertain estimate → certain near price wins
///   low  usage  → deep  runway  → optimism  discounts  uncertain estimate → estimate price wins
/// </summary>
public class BatteryScenarioTests8
{
    // Seg layout used by both tests. The estimate at seg1 spans 4–24c (predicted 14c).
    // All other segments have locked prices (certain).
    private static List<EnergySegment> Segs() => new()
    {
        Seg(0, 40, buy: 13),                                                       // certain 13c
        Seg(1, 39, buy: 14, buyLocked: false, advBuy: Adv(4, 14, 24)),             // uncertain estimate
        Seg(2, 30, buy: 50),
        Seg(3, 20, buy: 50),
        Seg(4, 12, buy: 50),
        Seg(5,  9, buy: 50),
        Seg(6,  8, buy: 50),
        Seg(7,  7, buy: 50),
        Seg(8,  8, buy: 50),
    };

    [Fact]
    public void HighUsage_ShortRunway_PrefersCertainNearPrice()
    {
        // hourlyUsage=10 → short runway → pessimism inflates the estimate toward its High bound (24c)
        // → certain 13c at seg0 is cheaper → planner buys at seg0.
        var segs = Segs();
        BatteryPlanner.OptimiseSegments(segs, Cfg(), 10m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[0].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
    }

    [Fact]
    public void LowUsage_DeepRunway_LeansToOptimisticEstimate()
    {
        // hourlyUsage=1 → deep runway → optimism discounts the estimate toward its Low bound (4c)
        // → weighted estimate (~11c) beats the certain 13c → planner buys at seg1.
        var segs = Segs();
        BatteryPlanner.OptimiseSegments(segs, Cfg(), 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[1].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
    }
}
