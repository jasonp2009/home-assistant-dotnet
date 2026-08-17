using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// The inverter's per-segment charge/discharge rate caps the battery's net change for the segment it
/// acts on. Solar surplus and household load flow through that cap (via the grid), they don't stack on
/// top of it. The projection already bakes each segment's natural solar-minus-usage flow into its
/// charge, so a forced charge/discharge must apply only <c>rate - NaturalChargeDeltaKwh</c>, not the full
/// rate. These tests pin that: each action segment's net move equals the inverter rate regardless of its
/// solar/usage. Pre-fix the action stacked the full rate on top of the natural flow.
/// </summary>
public class BatteryNaturalDeltaTests
{
    // 1 kWh: the Cfg() charge/discharge rate (12 kW) over a 5-minute segment.
    private static decimal Step => Cfg().SegmentChargeAmountKwh;

    private static EnergySegment WithFlow(EnergySegment segment, decimal solarKwh = 0m, decimal usageKwh = 0m)
    {
        segment.SolarForecastKwh = solarKwh;
        segment.UsageKwh = usageKwh;
        return segment;
    }

    [Fact]
    public void Buy_OnSunnySegment_RaisesByRateNotByRatePlusSurplus()
    {
        // seg0 carries 0.5 kWh of net solar surplus. Charge dips just below Min at seg1, so a single buy
        // at the cheap seg0 fixes it. Forcing charge subsumes the surplus: seg0's net move is the rate
        // (1.0) — relative to its pre-solar predecessor (8.0 - 0.5 = 7.5) it lands at 8.5, with the grid
        // supplying 0.5 and the solar the other 0.5. The pre-fix code added the full rate on top of the
        // surplus, landing seg0 at 9.0.
        var segs = new List<EnergySegment>
        {
            WithFlow(Seg(0, 8.0m, buy: 5m), solarKwh: 0.5m),
            Seg(1, 7.8m, buy: 100m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1m);

        Assert.Equal(EnergySegmentAction.Buy, segs[0].Action);
        Assert.Equal(8.5m, segs[0].EstimatedBatteryChargeKwh, precision: 4);
        Assert.Equal(8.3m, segs[1].EstimatedBatteryChargeKwh, precision: 4); // tail shifted by rate - surplus = 0.5
    }

    [Fact]
    public void Sell_OnHighUsageSegment_DropsByRateNotByRatePlusUsage()
    {
        // seg0 draws 0.3 kWh of load. Charge is over Max and rising, so a single sell at the best-priced
        // seg0 relieves it. Forcing discharge moves the inverter rate (1.0) total and serves the load from
        // that discharge: relative to seg0's pre-usage predecessor (50.0 + 0.3 = 50.3) it lands at 49.3.
        // The pre-fix code subtracted the rate on top of the usage drain, landing seg0 at 49.0.
        var segs = new List<EnergySegment>
        {
            WithFlow(Seg(0, 50.0m, sell: 100m), usageKwh: 0.3m),
            Seg(1, 50.2m, sell: 5m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1m);

        Assert.Equal(EnergySegmentAction.Sell, segs[0].Action);
        Assert.Equal(49.3m, segs[0].EstimatedBatteryChargeKwh, precision: 4);
        Assert.Equal(49.5m, segs[1].EstimatedBatteryChargeKwh, precision: 4); // tail shifted by -(rate + usage) = -0.7
    }

    [Fact]
    public void Arbitrage_BuyLeg_OnSunnySegment_RaisesByRateNotByRatePlusSurplus()
    {
        // No boundary crossing (20/19 kWh are inside [8, 50]), so OptimiseSegments is a no-op and the
        // buy-low/sell-high pair is committed by arbitrage. The buy leg (seg0) carries 0.4 kWh of surplus,
        // so its forced charge nets the rate (1.0 - 0.4 = +0.6 to the level), landing at 20.6 rather than
        // the pre-fix 21.0. The sell leg has no flow, so it drops by the full rate.
        var segs = new List<EnergySegment>
        {
            WithFlow(Seg(0, 20m, buy: 5m), solarKwh: 0.4m),
            Seg(1, 19m, sell: 50m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1m);
        BatteryPlanner.ApplyArbitrage(segs, Cfg(), 1m);

        Assert.Equal(EnergySegmentAction.Buy, segs[0].Action);
        Assert.Equal(EnergySegmentAction.Sell, segs[1].Action);
        Assert.Equal(20.6m, segs[0].EstimatedBatteryChargeKwh, precision: 4);  // +0.6 from the buy leg
        Assert.Equal(18.6m, segs[1].EstimatedBatteryChargeKwh, precision: 4);  // +0.6 buy, then -1.0 sell
    }

    [Fact]
    public void Buy_RespectsBufferUsingTheCorrectedDelta_WhenUsagePresent()
    {
        // The near-top seg0 (48 kWh) is the cheapest place to buy, but it draws 0.3 kWh of load. Because a
        // forced charge serves that load from the grid (not the battery), seg0 would actually rise by
        // 1.3 kWh to 49.3 — past the one-step buffer below Max (49). The buffer predicate must predict the
        // post-buy level with the same subsumption, reject seg0, and buy a segment with real headroom.
        var segs = new List<EnergySegment>
        {
            WithFlow(Seg(0, 48m, buy: 1m), usageKwh: 0.3m),
            Seg(1, 40m, buy: 100m),
            Seg(2, 30m, buy: 100m),
            Seg(3, 20m, buy: 100m),
            Seg(4, 10m, buy: 100m),
            Seg(5, 8m,  buy: 100m),
            Seg(6, 6m,  buy: 100m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1m);

        var charged = segs.Where(s => s.Action == EnergySegmentAction.Buy).ToList();
        Assert.NotEmpty(charged);
        Assert.All(charged, b => Assert.True(
            b.EstimatedBatteryChargeKwh <= Cfg().MaxCapacity - Step + 0.0001m,
            $"A buy charged the projection to {b.EstimatedBatteryChargeKwh} kWh — past the one-step buffer below Max ({Cfg().MaxCapacity})."));
    }
}
