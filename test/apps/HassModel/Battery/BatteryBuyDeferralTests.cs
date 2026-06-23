using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Reproduces the daytime over-fill that seeds the near-full buy/sell churn seen on 2026-06-21. To cover
/// an evening shortfall the greedy solver buys at the cheapest segment in the window — which is "now",
/// during the day — even when the battery is already near full and a near-full peak sits just ahead.
/// Charging now (Reserve mode pulls from the grid) walks the battery up against Max across successive
/// replans, leaving no room for solar, which then trips the Max boundary and forces a low-price sell.
///
/// The fix bounds the search window in <see cref="BatteryPlanner.GetPreviousBoundaryCrossingIndex"/> at
/// the band top (MaxCapacity - one step) rather than the raw MaxCapacity, so a buy can't be placed before
/// a segment it would hold over the band. With a near-full peak ahead, "now" is excluded and the
/// deficit-covering buy defers toward the evening (where the battery has drained and has room) — or solar
/// covers the day. The executed action for "now" is then None, not a grid charge that over-fills.
/// </summary>
public class BatteryBuyDeferralTests
{
    [Fact]
    public void BuyForEveningDeficit_IsDeferred_WhenChargingNowWouldHoldOverTheBand()
    {
        // SoC is 47 now (cheapest place to buy, 5c). A near-full peak (48.5) sits one segment ahead, then
        // the battery drains to a dip below Min in the "evening". Buying now would hold the extra kWh
        // through that peak, pinning it against Max — the over-fill that forces the bad sells. The solver
        // should leave "now" alone and place the buy later, where there is real headroom.
        var segs = new List<EnergySegment>
        {
            Seg(0, 47m,   buy: 5m),    // now — cheapest, but near full
            Seg(1, 48.5m, buy: 100m),  // near-full peak just ahead
            Seg(2, 46m,   buy: 100m),
            Seg(3, 38m,   buy: 100m),
            Seg(4, 28m,   buy: 100m),
            Seg(5, 18m,   buy: 100m),
            Seg(6, 10m,   buy: 100m),
            Seg(7, 7m,    buy: 100m),  // dips below Min
            Seg(8, 6m,    buy: 100m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1m);

        // "Now" must not be grid-charged while near full with a peak ahead — charging now is what walks the
        // battery into the Max boundary across replans and forces the low-price sell.
        Assert.NotEqual(EnergySegmentAction.Buy, segs[0].Action);
        // The deficit is still covered — the buy is deferred toward the evening, not dropped.
        Assert.Contains(segs, s => s.Action == EnergySegmentAction.Buy);
    }
}
