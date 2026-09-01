using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Covers the blind grid-charging seen on 2026-09-01 01:10–01:50 (and, by the "Buy at 0c" signature,
/// repeatedly on 2026-08-30). A DNS outage on the host made both api.amber.com.au and api.forecast.solar
/// unresolvable, so <c>ApplyPrice</c> left every segment's BuyPricePerKw null and <c>ApplySolarForecast</c>
/// left the horizon at zero solar.
///
/// With no solar the projection declines monotonically and trips the MIN boundary, so the solver looked for
/// somewhere to buy. <see cref="EnergySegmentExtensions.WeightedPrice"/> ranks a segment it cannot price at
/// decimal.MaxValue, which defers a buy only while some rival IS priced; with nothing priced every candidate
/// tied and <c>MinBy</c> returned the FIRST element — "now". The planner therefore grid-charged immediately,
/// at an unknown price, on every 5-minute replan until connectivity returned.
///
/// The fix excludes unpriced segments from the candidate sets outright (BatteryPlanner.CanBuy/CanSell), so
/// missing price data produces no action at all.
/// </summary>
public class BatteryMissingPriceTests
{
    /// <summary>72 segments of pure drain toward the floor, mirroring the 01:10 tick (SoC 31.3 kWh).</summary>
    private static List<EnergySegment> DecliningUnpricedHorizon()
    {
        var segs = Enumerable.Range(0, 72)
            .Select(i => Seg(i, 31.3m - i * 0.4m, buy: null, sell: null))
            .ToList();
        // ApplyPrice never ran, so the estimate flags keep their defaults and no advanced band is set.
        foreach (var s in segs) { s.IsBuyEstimate = true; s.IsSellEstimate = true; }
        return segs;
    }

    [Fact]
    public void NoPricesAnywhere_TakesNoAction()
    {
        var segs = DecliningUnpricedHorizon();

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1.7m);

        // Better no action than a bad action: with nothing priced the plan stays empty rather than
        // grid-charging "now" at an unknown price.
        Assert.All(segs, s => Assert.Equal(EnergySegmentAction.None, s.Action));
    }

    [Fact]
    public void NoPricesAnywhere_ArbitrageAlsoTakesNoAction()
    {
        var segs = DecliningUnpricedHorizon();

        BatteryPlanner.ApplyArbitrage(segs, Cfg(), hourlyUsage: 1.7m);

        Assert.All(segs, s => Assert.Equal(EnergySegmentAction.None, s.Action));
    }

    [Fact]
    public void OverMaxWithNoSellPrice_DoesNotDischarge()
    {
        // Sell-side counterpart: the projection climbs through MaxCapacity but no segment can be priced,
        // so the solver must hold rather than dump at an unknown price.
        var segs = Enumerable.Range(0, 72)
            .Select(i => Seg(i, 40m + i * 0.4m, buy: null, sell: null))
            .ToList();
        foreach (var s in segs) { s.IsBuyEstimate = true; s.IsSellEstimate = true; }

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1.7m);

        Assert.All(segs, s => Assert.Equal(EnergySegmentAction.None, s.Action));
    }

    [Fact]
    public void PricedSegmentIsStillUsed_AndUnpricedNowIsLeftAlone()
    {
        // The gate must not stop the planner working on the data it does have: one priced segment in an
        // otherwise unpriced horizon is still bought, and "now" (unpriced) is left alone. Before the fix
        // the solver bought the priced segment AND then fell through to the unpriced "now".
        var segs = DecliningUnpricedHorizon();
        segs[40].BuyPricePerKw = 9m;
        segs[40].IsBuyEstimate = false; // locked price, passes through WeightedPrice at face value

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1.7m);

        Assert.Equal(EnergySegmentAction.Buy, segs[40].Action);
        Assert.Equal(EnergySegmentAction.None, segs[0].Action);
        Assert.Single(segs, s => s.Action == EnergySegmentAction.Buy);
    }

    [Fact]
    public void EstimateBeyondAdvancedHorizon_IsNotActedOn()
    {
        // A price with no advanced (ML) band is a raw base forecast past Amber's ~24h horizon. WeightedPrice
        // already documents that such a segment must not be acted on; the candidate filter now enforces it
        // instead of relying on it being outranked.
        var segs = DecliningUnpricedHorizon();
        foreach (var s in segs)
        {
            s.BuyPricePerKw = 9m;
            s.IsBuyEstimate = true;
            s.AdvancedBuyPrice = null; // beyond the advanced horizon
        }

        BatteryPlanner.OptimiseSegments(segs, Cfg(), hourlyUsage: 1.7m);

        Assert.All(segs, s => Assert.Equal(EnergySegmentAction.None, s.Action));
    }
}
