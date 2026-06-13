using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Scenario tests focused on neutral-band weighting, null-price exclusion, and sell-during-demand-window behaviour.
/// </summary>
public class BatteryScenarioTests7
{
    private const decimal Usage = 1m;

    [Fact]
    public void NeutralBandBuy_RanksByPredictedPrice()
    {
        // seg0 (charge 33) and seg1 (charge 32) both sit in the neutral runway band (hours-to-empty
        // = charge - 8: 25h and 24h respectively, both in the neutral 20<h<26 zone → weight 0,
        // so the planner uses the predicted price directly). seg0's predicted price (10) < seg1's (12),
        // so the forced buy (driven by the dip at seg7 below min) must land on seg0.
        var segs = new List<EnergySegment>
        {
            Seg(0, 33, buy: 10, buyLocked: false, advBuy: Adv(9, 10, 11)),
            Seg(1, 32, buy: 12, buyLocked: false, advBuy: Adv(11, 12, 13)),
            Seg(2, 28, buy: 50),
            Seg(3, 20, buy: 50),
            Seg(4, 12, buy: 50),
            Seg(5,  9, buy: 50),
            Seg(6,  8, buy: 50),
            Seg(7,  7, buy: 50),
            Seg(8,  8, buy: 50),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        Assert.True(segs[0].Action == EnergySegmentAction.Buy,
            $"actions=[{actions}] charges=[{charges}]");
    }

    [Fact]
    public void NullPriceSegment_NotChosenForBuy()
    {
        // seg1 has no buy price (null). The dip at seg3 below min forces a buy; the null-price
        // segment must never be selected — decimal.MaxValue is used as its weighted cost so MinBy
        // always prefers any priced segment. A buy is placed, but not on seg1.
        var segs = new List<EnergySegment>
        {
            Seg(0, 10, buy: 10),
            Seg(1,  9),           // no buy price — null
            Seg(2,  8, buy: 10),
            Seg(3,  7, buy: 10),
            Seg(4,  8, buy: 10),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        Assert.True(
            segs[1].Action != EnergySegmentAction.Buy && segs.Any(s => s.Action == EnergySegmentAction.Buy),
            $"actions=[{actions}] charges=[{charges}]");
    }

    [Fact]
    public void SellDuringDemandWindow_IsAllowed()
    {
        // All segments are in a demand window. The projected charge starts above MaxCapacity and
        // must be relieved. The sell candidate filter does NOT exclude demand windows (unlike the
        // buy filter), so a Sell must be placed — discharging into a demand window is permitted.
        var segs = new List<EnergySegment>
        {
            Seg(0, 50, sell: 20m, demand: true),
            Seg(1, 51, sell: 20m, demand: true),
            Seg(2, 52, sell: 20m, demand: true),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        Assert.True(segs.Any(s => s.Action == EnergySegmentAction.Sell),
            $"actions=[{actions}] charges=[{charges}]");
    }
}
