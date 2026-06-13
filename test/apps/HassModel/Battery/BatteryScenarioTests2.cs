using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

public class BatteryScenarioTests2
{
    private const decimal Usage = 1m;

    // 1) KnownIssue: no boundary crossing → planner won't buy cheap seg proactively
    [Fact]
    [Trait("Category", "KnownIssue")]
    public void Arbitrage_BuysCheapNowWhenNoBoundaryCrossing()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 25, buy: 2),
            Seg(1, 24, buy: 30),
            Seg(2, 23, buy: 30),
            Seg(3, 22, buy: 30),
            Seg(4, 21, buy: 30),
            Seg(5, 20, buy: 30),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));

        Assert.True(segs[0].Action == EnergySegmentAction.Buy,
            $"actions=[{actions}] charges=[{charges}]");
    }

    // 2) KnownIssue: negative price (being paid to consume) → planner won't charge proactively
    [Fact]
    [Trait("Category", "KnownIssue")]
    public void NegativePrice_ChargesWhenPaidToConsume()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 25, buy: -10),
            Seg(1, 24, buy: 20),
            Seg(2, 23, buy: 20),
            Seg(3, 22, buy: 20),
            Seg(4, 21, buy: 20),
            Seg(5, 20, buy: 20),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));

        Assert.True(segs[0].Action == EnergySegmentAction.Buy,
            $"actions=[{actions}] charges=[{charges}]");
    }

    // 3) Forced buy: planner picks cheapest early segment over expensive forced moment
    [Fact]
    public void ForcedBuy_PrefersCheapEarlySegment_OverExpensiveForcedMoment()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 12, buy: 5),
            Seg(1, 11, buy: 40),
            Seg(2, 10, buy: 40),
            Seg(3, 9,  buy: 40),
            Seg(4, 8,  buy: 40),
            Seg(5, 7,  buy: 40),
            Seg(6, 8,  buy: 40),
            Seg(7, 9,  buy: 40),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));

        Assert.True(segs[0].Action == EnergySegmentAction.Buy,
            $"actions=[{actions}] charges=[{charges}]");
    }

    // 4) KnownIssue: charge jumps 48→52 in one step, detection misses the over-limit crossing
    [Fact]
    [Trait("Category", "KnownIssue")]
    public void OverchargeJump_StaysWithinMax()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 48, sell: 15),
            Seg(1, 52, sell: 15),
            Seg(2, 52, sell: 15),
            Seg(3, 52, sell: 15),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));

        Assert.All(segs, s => Assert.True(
            s.EstimatedBatteryChargeKwh <= Cfg().MaxCapacity + 0.001m,
            $"actions=[{actions}] charges=[{charges}]"));
    }

    // 5) KnownIssue: charge jumps 10→6 in one step, detection misses the under-limit crossing
    [Fact]
    [Trait("Category", "KnownIssue")]
    public void UnderchargeJump_StaysAboveMin()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 10, buy: 11),
            Seg(1, 6,  buy: 11),
            Seg(2, 6,  buy: 11),
            Seg(3, 6,  buy: 11),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));

        Assert.All(segs, s => Assert.True(
            s.EstimatedBatteryChargeKwh >= Cfg().MinCapacity - 0.001m,
            $"actions=[{actions}] charges=[{charges}]"));
    }

    // 6) Flat at min reserve → no action needed (should PASS)
    [Fact]
    public void FlatAtMin_NoThrash()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 8, buy: 11),
            Seg(1, 8, buy: 11),
            Seg(2, 8, buy: 11),
            Seg(3, 8, buy: 11),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));

        Assert.All(segs, s => Assert.True(s.Action != EnergySegmentAction.Buy,
            $"actions=[{actions}] charges=[{charges}]"));
    }
}
