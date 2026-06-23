using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Reproduces the seed of the buy/sell loop. Today the boundary solver may charge a segment up to
/// EXACTLY <c>MaxCapacity</c> (or discharge down to EXACTLY <c>MinCapacity</c>) — the buy feasibility
/// is <c>charge + step &lt;= MaxCapacity</c> and the sell feasibility is <c>charge - step &gt;= MinCapacity</c>
/// — which parks the projection right on the OPPOSITE action's trigger. One step later (a little solar,
/// or the next 5-minute re-plan) that segment reads "at max and rising" / "at min and falling" and the
/// solver reverses the action. So a single charge/discharge directly causes the opposite boundary
/// crossing — the buy/sell loop.
///
/// The fix reserves a one-step buffer (<c>SegmentChargeAmountKwh</c> / <c>SegmentDischargeAmountKwh</c>):
/// the solver charges only to <c>MaxCapacity - step</c> and discharges only to <c>MinCapacity + step</c>,
/// so the battery can reach a trigger only from solar (over max) or usage (under min) — never from the
/// solver's own action.
/// </summary>
public class BatteryBufferTests
{
    // 1 kWh: the Cfg() charge/discharge rate (12 kW) over a 5-minute segment.
    private static decimal Step => Cfg().SegmentChargeAmountKwh;

    [Fact]
    public void OptimiseSegments_DoesNotChargeOntoTheSellTrigger()
    {
        // Charge falls below Min far out, so a buy is needed. The near-max segment at index 0 (49 kWh)
        // is the cheapest place to buy — but buying it lands the projection on MaxCapacity (50), the
        // sell trigger. The solver should buy a segment with real headroom instead.
        var segs = new List<EnergySegment>
        {
            Seg(0, 49m, buy: 1m),
            Seg(1, 40m, buy: 100m),
            Seg(2, 30m, buy: 100m),
            Seg(3, 20m, buy: 100m),
            Seg(4, 10m, buy: 100m),
            Seg(5, 8m,  buy: 100m),
            Seg(6, 6m,  buy: 100m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1m);

        var charged = segs.Where(s => s.Action == EnergySegmentAction.Buy).ToList();
        Assert.All(charged, b => Assert.True(
            b.EstimatedBatteryChargeKwh <= Cfg().MaxCapacity - Step + 0.0001m,
            $"A buy charged the projection to {b.EstimatedBatteryChargeKwh} kWh — onto/over the sell trigger ({Cfg().MaxCapacity})."));
    }

    [Fact]
    public void OptimiseSegments_DoesNotDischargeOntoTheBuyTrigger()
    {
        // Charge rises over Max, so a sell is needed. The near-min segment at index 0 (9 kWh) is the
        // best-priced place to sell — but selling it lands the projection on MinCapacity (8), the buy
        // trigger. The solver should sell a segment with real headroom instead.
        var segs = new List<EnergySegment>
        {
            Seg(0, 9m,  sell: 100m),
            Seg(1, 20m, sell: 1m),
            Seg(2, 35m, sell: 1m),
            Seg(3, 50m, sell: 1m),
            Seg(4, 51m, sell: 1m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1m);

        var discharged = segs.Where(s => s.Action == EnergySegmentAction.Sell).ToList();
        Assert.All(discharged, s => Assert.True(
            s.EstimatedBatteryChargeKwh >= Cfg().MinCapacity + Step - 0.0001m,
            $"A sell discharged the projection to {s.EstimatedBatteryChargeKwh} kWh — onto/under the buy trigger ({Cfg().MinCapacity})."));
    }

    [Fact]
    public void ActNow_DoesNotBuyOntoMaxThenSell_AcrossConsecutiveCycles()
    {
        // Cycle 1: SoC 49 kWh with a far-future shortfall. Today the solver buys "now" (49 -> 50),
        // parking the battery on the sell trigger.
        var cycle1 = new List<EnergySegment>
        {
            Seg(0, 49m, buy: 1m),
            Seg(1, 40m, buy: 100m),
            Seg(2, 30m, buy: 100m),
            Seg(3, 20m, buy: 100m),
            Seg(4, 10m, buy: 100m),
            Seg(5, 8m,  buy: 100m),
            Seg(6, 6m,  buy: 100m),
        };
        BatteryPlanner.OptimiseSegments(cycle1, Cfg(), hourlyUsage: 1m);
        BatteryPlanner.ApplyArbitrage(cycle1, Cfg());

        // Cycle 2 (5 minutes later): that 1 kWh has materialised — SoC is now 50 — and solar nudges it
        // up, so "now" reads at-max-and-rising and the solver sells. Buying onto max last cycle directly
        // produced this sell: a single charge caused the opposite crossing.
        var cycle2 = new List<EnergySegment>
        {
            Seg(0, 50m,   sell: 5m),
            Seg(1, 50.5m, sell: 5m),
            Seg(2, 50m,   sell: 5m),
        };
        BatteryPlanner.OptimiseSegments(cycle2, Cfg(), hourlyUsage: 1m);
        BatteryPlanner.ApplyArbitrage(cycle2, Cfg());

        Assert.False(
            cycle1[0].Action == EnergySegmentAction.Buy && cycle2[0].Action == EnergySegmentAction.Sell,
            $"Charged onto the max trigger then sold the next cycle (buy/sell loop): " +
            $"cycle1 now={cycle1[0].Action}, cycle2 now={cycle2[0].Action}.");
    }
}
