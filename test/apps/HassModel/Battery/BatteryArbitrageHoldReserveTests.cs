using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Unit tests for the hold-window drain reserve in <see cref="BatteryPlanner"/>'s feasibility check
/// (<c>ArbitrageHoldDrainReserveFraction</c>). A SELL-BEFORE-BUY pair's premise is that the battery survives
/// on its own until the booked refill; what threatens that is error in the projected household drain, which
/// grows with how long the pair holds. The old fixed one-step buffer did not grow at all.
///
/// The point of these tests is that the reserve DISCRIMINATES by hold length. A guard that simply rejected
/// every sell-before-buy pair would also have stopped the losing trade, so the short-hold and reserve-off
/// controls below are what give the long-hold rejection its meaning.
/// </summary>
public class BatteryArbitrageHoldReserveTests
{
    private static BatteryConfig ReserveCfg(decimal fraction)
    {
        var cfg = Cfg(); // Min 8, Max 50, 1 kWh per segment, margin 0
        cfg.ArbitrageHoldDrainReserveFraction = fraction;
        return cfg;
    }

    /// <summary>
    /// Flat, comfortably in-bounds trajectory (no boundary work to do), one dear sell and one cheap buy so
    /// the pair is forced, and a fixed per-segment drain so the reserve is easy to reason about.
    /// </summary>
    private static List<EnergySegment> Scenario(int buyIndex, decimal usagePerSegment, int count = 21)
    {
        var segs = new List<EnergySegment>();
        for (var i = 0; i < count; i++)
        {
            var seg = Seg(i, 15m,
                sell: i == 0 ? 40m : null,
                buy: i == buyIndex ? 5m : null);
            seg.UsageKwh = usagePerSegment;
            segs.Add(seg);
        }
        return segs;
    }

    private static (bool Sold, bool Bought) Run(List<EnergySegment> segs, BatteryConfig cfg)
    {
        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);
        return (segs[0].Action == EnergySegmentAction.Sell,
                segs.Any(s => s.Action == EnergySegmentAction.Buy));
    }

    /// <summary>
    /// A 3-segment hold carries 1.5 kWh of projected drain, so at a 1.0 fraction it reserves 1.5 kWh on top
    /// of the one-step buffer — easily covered at 15 kWh with a floor of 8. The pair must still commit:
    /// short round trips are exactly what arbitrage is for.
    /// </summary>
    [Fact]
    public void ShortHold_StillCommits()
    {
        var (sold, bought) = Run(Scenario(buyIndex: 3, usagePerSegment: 0.5m), ReserveCfg(1.0m));
        Assert.True(sold, "a short-hold sell-before-buy pair should survive the reserve");
        Assert.True(bought);
    }

    /// <summary>
    /// A 20-segment hold at the same drain carries 10 kWh, so the reserve demands ~10 kWh of headroom above
    /// the floor that the trajectory (15 kWh, floor 8) does not have. The pair must be rejected — this is
    /// the 2026-08-12 morning shape in miniature.
    /// </summary>
    [Fact]
    public void LongHold_Rejected()
    {
        var (sold, _) = Run(Scenario(buyIndex: 20, usagePerSegment: 0.5m), ReserveCfg(1.0m));
        Assert.False(sold, "a long-hold sell-before-buy pair should be rejected by the drain reserve");
    }

    /// <summary>
    /// CONTROL for <see cref="LongHold_Rejected"/>: the identical scenario with the reserve switched off
    /// commits. Without this, that test would also pass if the pair were being rejected for some unrelated
    /// reason (bad prices, an infeasible level, the margin gate) and would prove nothing about the reserve.
    /// </summary>
    [Fact]
    public void LongHold_CommitsWhenReserveDisabled()
    {
        var (sold, bought) = Run(Scenario(buyIndex: 20, usagePerSegment: 0.5m), ReserveCfg(0m));
        Assert.True(sold, "with the reserve off the long-hold pair should commit, as it did before the fix");
        Assert.True(bought);
    }

    /// <summary>
    /// The reserve is scaled by the DRAIN, not merely by the number of segments held. Same 20-segment hold,
    /// a tenth of the drain: the pair becomes affordable again. Pins that a long but quiet hold (e.g.
    /// overnight, or a household that barely consumes) is not penalised for its length alone.
    /// </summary>
    [Fact]
    public void LongButLowDrainHold_StillCommits()
    {
        var (sold, bought) = Run(Scenario(buyIndex: 20, usagePerSegment: 0.05m), ReserveCfg(1.0m));
        Assert.True(sold, "a long hold carrying little drain should still be affordable");
        Assert.True(bought);
    }

    /// <summary>
    /// BUY-BEFORE-SELL must be untouched. Its risk is over-filling, and a drain UNDER-estimate makes that
    /// less likely, not more — so the Max-side check gets no reserve. Charge-first pairs are also the
    /// direction that still makes money when feed-in sits below import, so silently suppressing them would
    /// be a real regression.
    /// </summary>
    [Fact]
    public void BuyBeforeSell_Unaffected()
    {
        var cfg = ReserveCfg(1.0m);
        var segs = new List<EnergySegment>();
        for (var i = 0; i < 21; i++)
        {
            var seg = Seg(i, 15m, buy: i == 0 ? 5m : null, sell: i == 20 ? 40m : null);
            seg.UsageKwh = 0.5m; // same heavy drain that rejects the sell-before-buy pair above
            segs.Add(seg);
        }
        BatteryPlanner.OptimiseSegments(segs, cfg, 1m);
        BatteryPlanner.ApplyArbitrage(segs, cfg, 1m);

        Assert.Equal(EnergySegmentAction.Buy, segs[0].Action);
        Assert.Equal(EnergySegmentAction.Sell, segs[20].Action);
    }
}
