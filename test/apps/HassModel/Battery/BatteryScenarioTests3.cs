using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

public class BatteryScenarioTests3
{
    private const decimal Usage = 1m;

    [Fact]
    public void DemandWindow_BuysInNonDemandGap()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 10, buy: 11, demand: true),
            Seg(1,  9, buy: 11, demand: true),
            Seg(2,  8, buy:  9, demand: false),
            Seg(3,  7, buy: 11, demand: true),
            Seg(4,  8, buy: 11, demand: true),
            Seg(5,  9, buy: 11, demand: true),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var msg = $"actions=[{actions}] charges=[{charges}]";

        Assert.True(segs[2].Action == EnergySegmentAction.Buy, msg);
    }

    [Fact]
    public void MultiCrossing_MinThenMax_StaysInBounds()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 12, buy: 10, sell: 20),
            Seg(1, 10, buy: 10, sell: 20),
            Seg(2,  8, buy: 10, sell: 20),
            Seg(3,  7, buy: 10, sell: 20),
            Seg(4,  8, buy: 10, sell: 20),
            Seg(5, 30, buy: 10, sell: 20),
            Seg(6, 45, buy: 10, sell: 20),
            Seg(7, 50, buy: 10, sell: 20),
            Seg(8, 51, buy: 10, sell: 20),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var msg = $"actions=[{actions}] charges=[{charges}]";

        Assert.True(
            segs.All(s => s.EstimatedBatteryChargeKwh >= Cfg().MinCapacity - 0.01m &&
                          s.EstimatedBatteryChargeKwh <= Cfg().MaxCapacity + 0.01m),
            msg);
    }

    [Fact]
    public void Overcharge_SellsAtHighestFeedInInWindow()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 50, sell:  5),
            Seg(1, 51, sell: 30),
            Seg(2, 52, sell: 10),
            Seg(3, 53, sell:  8),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var msg = $"actions=[{actions}] charges=[{charges}]";

        Assert.True(segs[1].Action == EnergySegmentAction.Sell, msg);
    }

    [Fact]
    public void Pessimism_PicksCheaperLockedPrice_NotOverpaying()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 9, buy: 20),
            Seg(1, 8, buy: 10),
            Seg(2, 7, buy: 25),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var msg = $"actions=[{actions}] charges=[{charges}]";

        Assert.True(segs[1].Action == EnergySegmentAction.Buy, msg);
    }

    [Fact]
    public void DeepDepletion_BuysAtThreeCheapestSegments()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0,  11, buy:  5),
            Seg(1,  10, buy:  6),
            Seg(2,   9, buy:  7),
            Seg(3,   8, buy: 50),
            Seg(4,   7, buy: 50),
            Seg(5,   6, buy: 50),
            Seg(6,   5, buy: 50),
            Seg(7,   6, buy: 50),
            Seg(8,   7, buy: 50),
            Seg(9,   8, buy: 50),
            Seg(10,  9, buy: 50),
            Seg(11, 10, buy: 50),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var msg = $"actions=[{actions}] charges=[{charges}]";

        Assert.True(
            segs[0].Action == EnergySegmentAction.Buy &&
            segs[1].Action == EnergySegmentAction.Buy &&
            segs[2].Action == EnergySegmentAction.Buy,
            msg);
    }
}
