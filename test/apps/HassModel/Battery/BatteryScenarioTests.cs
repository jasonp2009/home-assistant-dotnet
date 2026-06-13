using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Real-world scenario tests for the greedy planner (<see cref="BatteryPlanner.OptimiseSegments"/>).
/// These assert the behaviour we *want*; tests tagged Category=KnownIssue are expected to fail and
/// flag a behaviour worth discussing (run the green subset with: dotnet test --filter Category!=KnownIssue).
///
/// Feed-in note: `sell` is SellPricePerKw as ApplyPrice stores it — a normal feed-in earning is
/// POSITIVE (Amber reports it negative; ApplyPrice negates). A negative `sell` = paying to export.
/// </summary>
public class BatteryScenarioTests
{
    private const decimal Usage = 1m; // kWh/h used for the runway weighting (charge - 8 == hours-to-empty)

    // -------------------------------------------------------------------------------------------
    // Buying
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Buys_AtCheapestSegment_WhenDepletionForcesASingleBuy()
    {
        // Charge dips one kWh below the minimum once, then recovers. All prices are locked (certain),
        // so the planner should simply buy at the cheapest segment in the window.
        var segs = new List<EnergySegment>
        {
            Seg(0, 10, buy: 15),
            Seg(1, 9,  buy: 11), // cheapest
            Seg(2, 8,  buy: 13),
            Seg(3, 7,  buy: 14),
            Seg(4, 8,  buy: 20),
            Seg(5, 9,  buy: 20),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.Equal(EnergySegmentAction.Buy, segs[1].Action);
        Assert.Single(segs.Where(s => s.Action == EnergySegmentAction.Buy));
    }

    [Fact]
    public void LowBattery_PrefersCertainCurrentPrice_OverOptimisticFutureEstimate()
    {
        // The headline fix: when the battery is low (short runway → pessimistic), an estimated
        // near-future price that *could* be cheap must not lure the planner away from a good, certain
        // price now. seg0 is a locked 12c; seg1 is an estimate predicted at 11c but uncertain (5–25c).
        var segs = new List<EnergySegment>
        {
            Seg(0, 9, buy: 12, buyLocked: true),
            Seg(1, 8, buy: 11, buyLocked: false, advBuy: Adv(low: 5, predicted: 11, high: 25)),
            Seg(2, 7, buy: 18, buyLocked: true),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.Equal(EnergySegmentAction.Buy, segs[0].Action);
        Assert.NotEqual(EnergySegmentAction.Buy, segs[1].Action);
    }

    [Fact]
    [Trait("Category", "KnownIssue")]
    public void DeepRunway_DoesNotSkipCertainPrice_ForOptimisticEstimate()
    {
        // The same failure mode as the deployed code originally had, but in the deep-runway case.
        // Early in the buy window the battery still has lots of runway, so the optimism discount kicks
        // in and makes an uncertain estimate (predicted 14c, but as low as 4c) look cheaper than a
        // certain 12c now — even though its *expected* price is higher. The good certain price gets skipped.
        // (Charge trajectory is compressed to keep the test short; in reality the crossing is hours away.)
        var segs = new List<EnergySegment>
        {
            Seg(0, 45, buy: 12, buyLocked: true),
            Seg(1, 44, buy: 14, buyLocked: false, advBuy: Adv(low: 4, predicted: 14, high: 24)),
            Seg(2, 40, buy: 50),
            Seg(3, 30, buy: 50),
            Seg(4, 20, buy: 50),
            Seg(5, 12, buy: 50),
            Seg(6, 9,  buy: 50),
            Seg(7, 8,  buy: 50),
            Seg(8, 7,  buy: 50),
            Seg(9, 8,  buy: 50),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.Equal(EnergySegmentAction.Buy, segs[0].Action);   // expected: take the certain 12c
        Assert.NotEqual(EnergySegmentAction.Buy, segs[1].Action); // not the optimistic 14c estimate
    }

    [Fact]
    public void DemandWindowOnly_DoesNotBuy_ReliesOnGrid()
    {
        // Intentional behaviour (confirmed by owner): the planner never charges during a demand window —
        // those windows carry excess usage charges, so the home draws from the grid instead. If the whole
        // pre-depletion window is demand-windowed it places no Buy; the projected charge dropping below the
        // modelled reserve here is expected and is covered by grid import in reality.
        var segs = new List<EnergySegment>
        {
            Seg(0, 10, buy: 11, demand: true),
            Seg(1, 9,  buy: 11, demand: true),
            Seg(2, 8,  buy: 11, demand: true),
            Seg(3, 7,  buy: 11, demand: true),
            Seg(4, 6,  buy: 11, demand: true),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.DoesNotContain(segs, s => s.Action == EnergySegmentAction.Buy);
    }

    [Fact]
    public void ComfortableBattery_WithinBounds_TakesNoAction()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 30, buy: 10, sell: 10m),
            Seg(1, 28, buy: 10, sell: 10m),
            Seg(2, 26, buy: 10, sell: 10m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.All(segs, s => Assert.Equal(EnergySegmentAction.None, s.Action));
    }

    // -------------------------------------------------------------------------------------------
    // Selling / solar
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void HighSolar_KeepsBatteryWithinMax_ByDischarging()
    {
        // A strong solar forecast pushes projected charge up through and past MaxCapacity. The planner
        // should discharge enough that the battery is not left over-charged.
        var segs = new List<EnergySegment>
        {
            Seg(0, 49, sell: 15m),
            Seg(1, 50, sell: 15m),
            Seg(2, 51, sell: 15m),
            Seg(3, 52, sell: 15m),
            Seg(4, 53, sell: 15m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.Contains(segs, s => s.Action == EnergySegmentAction.Sell);
        // Tolerance absorbs decimal rounding in SegmentDischargeAmountKwh (12 * 5/60 != exactly 1.0).
        Assert.All(segs, s => Assert.True(s.EstimatedBatteryChargeKwh <= Cfg().MaxCapacity + 0.001m,
            "projected charge stayed above MaxCapacity after optimisation"));
    }

    [Fact]
    public void StrongSolarIncoming_DoesNotBuyFromGrid()
    {
        // Battery is comfortable and a solar forecast will keep it rising, so there is no depletion to
        // cover — the planner should never buy from the grid here.
        var segs = new List<EnergySegment>
        {
            Seg(0, 40, buy: 8, sell: 12m),
            Seg(1, 42, buy: 8, sell: 12m),
            Seg(2, 45, buy: 8, sell: 12m),
            Seg(3, 47, buy: 8, sell: 12m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.DoesNotContain(segs, s => s.Action == EnergySegmentAction.Buy);
    }

    [Fact]
    public void Overcharge_DischargesAtHighestFeedInPrice()
    {
        // When forced to discharge to relieve an over-charge, the planner should pick the most
        // profitable moment — the HIGHEST feed-in earning. seg0 earns 30c, seg1/seg2 earn 10c.
        var segs = new List<EnergySegment>
        {
            Seg(0, 50, sell: 30m), // feed-in 30c — most profitable to discharge into
            Seg(1, 51, sell: 10m), // feed-in 10c
            Seg(2, 52, sell: 10m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.Equal(EnergySegmentAction.Sell, segs[0].Action);
    }

    [Fact]
    [Trait("Category", "KnownIssue")]
    public void Overcharge_DoesNotDischargeAtNegativeFeedIn()
    {
        // Economic probe: feed-in is negative (-5c) — exporting costs money. The battery is physically
        // capped anyway (solar would curtail), so force-discharging at a negative price destroys value.
        var segs = new List<EnergySegment>
        {
            Seg(0, 50, sell: -5m),
            Seg(1, 51, sell: -5m),
            Seg(2, 52, sell: -5m),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        Assert.DoesNotContain(segs, s => s.Action == EnergySegmentAction.Sell);
    }
}
