using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Unit tests for <see cref="BatteryPlanner.ApplyArbitrage"/>. Each test builds a segment list,
/// runs OptimiseSegments (so the post-condition baseline matches production call order), then
/// ApplyArbitrage, and asserts the resulting actions.
/// </summary>
public class BatteryArbitrageTests
{
    // -----------------------------------------------------------------------------------------
    // 1. CommitsProfitablePair
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CommitsProfitablePair()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 5),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 40m),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[2].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
        Assert.True(segs[5].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 2. ThinSpread_NoCommit
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ThinSpread_NoCommit()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 35),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 38m),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 3. MarginAndEfficiency_BlockMarginalPair
    //    Gate: 30/0.9 + 5 = 38.33 > 38 → no commit
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void MarginAndEfficiency_BlockMarginalPair()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 30),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 38m),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 4. Disabled_NoOp
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Disabled_NoOp()
    {
        var cfg = Cfg();
        cfg.ArbitrageEnabled = false;

        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 5),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 40m),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 5. Feasibility_RejectsOverfill
    //    All segs at 49.5 kWh; holding +1 kWh between buy(1) and sell(5) would reach 50.5 > Max(50).
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Feasibility_RejectsOverfill()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 49.5m),
            Seg(1, 49.5m, buy: 5),
            Seg(2, 49.5m),
            Seg(3, 49.5m),
            Seg(4, 49.5m),
            Seg(5, 49.5m, sell: 40m),
            Seg(6, 49.5m),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 6. SellBeforeBuy_Works
    //    Discharge dear at seg1 (from stored charge), refill cheap at seg4.
    //    Discharge region stays >= Min (25 - 1 = 24 >= 8).
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SellBeforeBuy_Works()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25, sell: 40m),
            Seg(2, 25),
            Seg(3, 25),
            Seg(4, 25, buy: 5),
            Seg(5, 25),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[1].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
        Assert.True(segs[4].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 7. UncertainPrices_GatedByPessimism
    //    Predicted: buy=10, sell=40 → would commit on predicted alone.
    //    Pessimistic: buy~24 (10*0.3 + 30*0.7), sell~19 (40*0.3 + 10*0.7).
    //    Gate: 19 >= 24/0.9 + 5 = 31.67 → FALSE → no commit.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void UncertainPrices_GatedByPessimism()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 10, buyLocked: false, advBuy: Adv(8, 10, 30)),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 40m, sellLocked: false, advSell: Adv(-42m, -40m, -10m)),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }
}
