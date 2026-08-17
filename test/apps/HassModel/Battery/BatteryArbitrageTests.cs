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
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

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
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

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
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

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
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

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
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

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
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[1].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
        Assert.True(segs[4].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 7. EstimatePessimism_DiscountsBothForecastLegs
    //    buy(seg2) before sell(seg5), BOTH estimates. Pessimism (0.5) discounts each forecast leg:
    //    buy = 10*0.5 + High 40*0.5 = 25; sell = 40*0.5 + low-earning 4*0.5 = 22.
    //    Gate (margin 0, eff 0.9): net = 22 - 25/0.9 = -5.78 < 0 → NO commit, even though face value
    //    (40 - 10/0.9 = 28.9) clears it easily. Pins that an all-forecast pair is judged on its
    //    pessimistic, not predicted, prices.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void EstimatePessimism_DiscountsBothForecastLegs()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 10, buyLocked: false, advBuy: Adv(8, 10, 40)),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 40m, sellLocked: false, advSell: Adv(-4m, -40m, -44m)),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 7b. BuyBeforeSell_LowerWeight_CommitsWhereSymmetricWouldNot
    //    SAME fixture as test 7 (which does NOT commit at the symmetric 0.5 weight), but with the buy-before-sell
    //    weight lowered to 0.25. buy = 10*0.75 + High 40*0.25 = 17.5; sell = 40*0.75 + low-earning 4*0.25 = 31.
    //    Gate (margin 0): net = 31 - 17.5/0.9 = 11.56 >= 0 → COMMITS. Pins that a charge-first pair the symmetric
    //    pessimism would have gated out is unlocked once buy-before-sell is judged less conservatively.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void BuyBeforeSell_LowerWeight_CommitsWhereSymmetricWouldNot()
    {
        var cfg = Cfg();
        cfg.ArbitrageBuyBeforeSellWeight = 0.25m; // less pessimistic than the 0.5 sell-before-buy weight
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 10, buyLocked: false, advBuy: Adv(8, 10, 40)),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 40m, sellLocked: false, advSell: Adv(-4m, -40m, -44m)),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[2].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
        Assert.True(segs[5].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 7c. SellBeforeBuy_IgnoresLowerBuyBeforeSellWeight
    //    SAME band values as test 7b but the SELL comes first (discharge now, refill later), so the lower
    //    buy-before-sell weight must NOT apply — the pair keeps the full 0.5 pessimism: buy = 10*0.5 + 40*0.5 = 25,
    //    sell = 40*0.5 + 4*0.5 = 22, net = 22 - 25/0.9 = -5.78 < 0 → NO commit. Pins that direction, not just the
    //    config value, selects the weight: sell-before-buy stays conservative even when the new knob is lowered.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SellBeforeBuy_IgnoresLowerBuyBeforeSellWeight()
    {
        var cfg = Cfg();
        cfg.ArbitrageBuyBeforeSellWeight = 0.25m; // would commit the SAME bands as a buy-before-sell pair
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25, sell: 40m, sellLocked: false, advSell: Adv(-4m, -40m, -44m)), // sell first
            Seg(2, 25),
            Seg(3, 25),
            Seg(4, 25, buy: 10, buyLocked: false, advBuy: Adv(8, 10, 40)),           // refill later
            Seg(5, 25),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 8. EstimatePessimism_GatesUncertainPair
    //    buy(seg2) before sell(seg5), both estimates. Both legs pessimised: buy = 20*0.5 + 30*0.5 = 25,
    //    sell = predicted 30*0.5 + low-earning 2*0.5 = 16.
    //    Gate (margin 0): net = 16 - 25/0.9 = -11.78 < 0 → NO commit — even though face value alone
    //    (30 - 20/0.9 = 7.78) would have cleared it. Proves the forecast pessimism still bites.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void EstimatePessimism_GatesUncertainPair()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 20, buyLocked: false, advBuy: Adv(18, 20, 30)),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 30m, sellLocked: false, advSell: Adv(-2m, -30m, -40m)),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 9. LockedLeg_PricedAtFaceValue_EstimateLegDiscounted
    //    sell(seg1, LOCKED) before buy(seg4, estimate). The locked sell keeps its face value = 30; only
    //    the estimate buy is pessimised: 10*0.5 + High 20*0.5 = 15.
    //    Gate (margin 0): net = 30 - 15/0.9 = 13.33 >= 0 → COMMITS. Pins that a materialised (locked) leg
    //    is NOT discounted while the forecast leg is — so a certain price now isn't penalised.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void LockedLeg_PricedAtFaceValue_EstimateLegDiscounted()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25, sell: 30m), // locked (default): priced at face value
            Seg(2, 25),
            Seg(3, 25),
            Seg(4, 25, buy: 10, buyLocked: false, advBuy: Adv(8, 10, 20)),
            Seg(5, 25),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[1].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
        Assert.True(segs[4].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 14. TakesCertainNowSell_OverHigherButUncertainForecast
    //    The 2026-06-22 spike-skip repro. A certain locked now-sell (22c) competes with a higher-PREDICTED
    //    future sell estimate (27c, but worst-earning only 10c) for one cheap buy. Ranked by pessimistic
    //    earning the certain 22c beats the discounted estimate (27*0.5 + 10*0.5 = 18.5), so the now-sell is
    //    taken and the uncertain forecast is passed over (it would have won on face value). Regression for
    //    the spike where good 22c sells were skipped to chase a receding predicted peak.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TakesCertainNowSell_OverHigherButUncertainForecast()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25, sell: 22m),                                                    // certain now-sell, locked
            Seg(1, 25, sell: 27m, sellLocked: false, advSell: Adv(-10m, -27m, -40m)), // future estimate: pred 27, worst 10
            Seg(2, 25),
            Seg(3, 25, buy: 5m),                                                      // single cheap buy
            Seg(4, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[0].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
        Assert.True(segs[1].Action == EnergySegmentAction.None, $"actions=[{actions}]");
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
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 11. Arbitrage_DoesNotBuyInDemandWindow
    //    The only cheap buy (seg2, 5c) is in a demand window. Buying in a demand window incurs extra
    //    charges and is disallowed, so the profitable sell (seg5) has no buy counterpart and nothing
    //    commits.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Arbitrage_DoesNotBuyInDemandWindow()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 5, demand: true),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 40m),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs.All(s => s.Action == EnergySegmentAction.None), $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 12. Arbitrage_AllowsSellInDemandWindow
    //    Selling in a demand window is fine; only buying is disallowed. Buy seg2 (non-demand, 5c),
    //    sell seg5 (demand, 40c) → commits.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Arbitrage_AllowsSellInDemandWindow()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 5),
            Seg(3, 25),
            Seg(4, 25),
            Seg(5, 25, sell: 40m, demand: true),
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[2].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
        Assert.True(segs[5].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
    }

    // -----------------------------------------------------------------------------------------
    // 13. BeyondHorizonEstimateLeg_DoesNotOverflow
    //    A buy candidate that is an estimate with NO advanced (ML) band (a forecast past Amber's ~24h
    //    advanced horizon) has WeightedPrice == decimal.MaxValue (the "un-actionable" sentinel). Before
    //    the fix, ApplyArbitrage computed buyCost/RoundTripEfficiency on that sentinel, overflowing the
    //    decimal range ("Value was either too large or too small for a Decimal") and aborting the whole
    //    plan. The un-actionable estimate (seg3) must be ignored, and the actionable locked pair
    //    (buy seg2 / sell seg5) must still commit.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void BeyondHorizonEstimateLeg_DoesNotOverflow()
    {
        var cfg = Cfg();
        var segs = new List<EnergySegment>
        {
            Seg(0, 25),
            Seg(1, 25),
            Seg(2, 25, buy: 5),                                   // actionable (locked) cheap buy
            Seg(3, 25, buy: 30, buyLocked: false, advBuy: null), // beyond-horizon estimate: no ML band
            Seg(4, 25),
            Seg(5, 25, sell: 40m),                               // actionable (locked) sell
            Seg(6, 25),
        };

        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[2].Action == EnergySegmentAction.Buy, $"actions=[{actions}]");
        Assert.True(segs[5].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
        Assert.True(segs[3].Action == EnergySegmentAction.None, $"actions=[{actions}]");
    }
}
