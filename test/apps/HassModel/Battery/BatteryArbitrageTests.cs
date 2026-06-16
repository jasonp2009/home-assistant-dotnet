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
    // 3. MarginGate_BlocksWhenBelowConfiguredMargin
    //    Deployed margin is 0; this test sets an explicit 10c margin to exercise that gate.
    //    buy 30, sell 35: without margin, 35 >= 30/0.9 = 33.33 would commit; the 10c margin lifts the
    //    threshold to 43.33, so it must NOT commit.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void MarginGate_BlocksWhenBelowConfiguredMargin()
    {
        var cfg = Cfg();
        cfg.ArbitrageMinMarginPerKwh = 10m;
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 30),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 35m),
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
    // 7. DirectionalPessimism_PricesEarlierLegAtFaceValue
    //    buy(seg2) before sell(seg5), both estimates. Pessimism (0.5) lands on the LATER leg only (the
    //    sell): 40*0.5 + 4*0.5 = 22. The earlier leg (buy) is priced at face value (predicted) = 10.
    //    Gate (margin 0, eff 0.9): net = 22 - 10/0.9 = 10.89 >= 0 → COMMITS.
    //    (The old both-legs-pessimised pricing used buy ~25 → 22 vs 25/0.9 = 27.78 → would NOT have
    //    committed; this pins the directional change.)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DirectionalPessimism_PricesEarlierLegAtFaceValue()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 10, buyLocked: false, advBuy: Adv(8, 10, 40)),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 40m, sellLocked: false, advSell: Adv(-44m, -40m, -4m)),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[2].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
        Assert.True(segs[5].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 8. LaterLegPessimism_StillGatesUncertainPair
    //    buy(seg2) before sell(seg5), both estimates. Earlier leg (buy) at face value = 20. Later leg
    //    (sell) pessimised: predicted 30, low-earning bound 2 → 30*0.5 + 2*0.5 = 16.
    //    Gate (margin 0): net = 16 - 20/0.9 = -6.22 < 0 → NO commit — even though face value alone
    //    (30 - 20/0.9 = 7.78) would have cleared it. Proves the later leg's pessimism still bites.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void LaterLegPessimism_StillGatesUncertainPair()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 20, buyLocked: false, advBuy: Adv(18, 20, 30)),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 30m, sellLocked: false, advSell: Adv(-40m, -30m, -2m)),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 9. SellBeforeBuy_PessimisesLaterBuyNotEarlierSell
    //    sell(seg1) before buy(seg4), both estimates. Here the BUY is the later leg. The earlier leg
    //    (sell) is priced at face value (predicted) = 30 — its poor pessimistic bound (2) is NOT used.
    //    Later leg (buy) pessimised: 10*0.5 + 20*0.5 = 15.
    //    Gate (margin 0): net = 30 - 15/0.9 = 13.33 >= 0 → COMMITS. (Had the sell been wrongly
    //    pessimised to 2, net would be -14.7 → blocked, so the commit proves the earlier sell is at
    //    face value.)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SellBeforeBuy_PessimisesLaterBuyNotEarlierSell()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25, sell: 30m, sellLocked: false, advSell: Adv(-35m, -30m, -2m)),
            Seg(2, 25),
            Seg(3, 25),
            Seg(4, 25, buy: 10, buyLocked: false, advBuy: Adv(8, 10, 20)),
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
    // 10. Margin2c_BlocksThinArbitrage
    //    Deployed margin is 2c; this pins that it blocks a thin spread. buy 30, sell 35 (locked):
    //    net = 35 - 30/0.9 = 1.67 < 2 → NO commit (would have committed at margin 0).
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Margin2c_BlocksThinArbitrage()
    {
        var cfg = Cfg();
        cfg.ArbitrageMinMarginPerKwh = 2m;
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 30),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 35m),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }
}
