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
/// Weighting/edge probes: estimate-sell sign handling, a forced buy inducing an undetected over-charge,
/// and steep deep depletion. KnownIssue probes are expected to fail.
/// </summary>
public class BatteryScenarioTests6
{
    private const decimal Usage = 1m;

    [Fact]
    public void EstimateSell_DischargesAtHighestExpectedFeedIn()
    {
        // Sell-side sign handling: feed-in advanced prices are stored as Amber reports them (negative).
        // seg0's expected earning (30c) is the highest, so it should be the discharge target even as an
        // uncertain estimate. Confirms GetWeightedPrice's negation/blend is correct for sells.
        var segs = new List<EnergySegment>
        {
            Seg(0, 50, sell: 30m, sellLocked: false, advSell: Adv(-32m, -30m, -28m)),
            Seg(1, 51, sell: 10m, sellLocked: false, advSell: Adv(-12m, -10m, -8m)),
            Seg(2, 52, sell: 10m, sellLocked: false, advSell: Adv(-12m, -10m, -8m)),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        Assert.True(segs[0].Action == EnergySegmentAction.Sell, $"actions=[{actions}]");
    }

    [Fact]
    [Trait("Category", "KnownIssue")]
    public void BuyToFixMin_InducesUndetectedOvercharge()
    {
        // A forced buy to avoid the early dip below min raises every later segment by ~1 kWh, pushing seg6
        // from 50 to ~51 — an over-charge that is LEFT UNRELIEVED. The max-crossing check needs the PRIOR
        // segment to be >= Max, but seg5 lands at ~49.9999 (also nudged by SegmentChargeAmountKwh, 12*5/60,
        // not being exactly 1.0), so the crossing is missed and no Sell is placed. Diagnostic charges:
        // [10,9,8,31,41,~50,~51], actions [Buy,None,None,None,None,None,None].
        var segs = new List<EnergySegment>
        {
            Seg(0,  9, buy: 10, sell: 20),
            Seg(1,  8, buy: 10, sell: 20),
            Seg(2,  7, buy: 10, sell: 20),
            Seg(3, 30, buy: 10, sell: 20),
            Seg(4, 40, buy: 10, sell: 20),
            Seg(5, 49, buy: 10, sell: 20),
            Seg(6, 50, buy: 10, sell: 20),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        Assert.True(segs.All(s => s.EstimatedBatteryChargeKwh <= Cfg().MaxCapacity + 0.01m),
            $"projected charge left above MaxCapacity. actions=[{actions}] charges=[{charges}]");
    }

    [Fact]
    [Trait("Category", "KnownIssue")]
    public void DeepDepletion_SteepDecline_LeavesSegmentBelowMin()
    {
        // A steep 2 kWh/segment decline (usage faster than the 1 kWh/segment charge rate). The greedy buys
        // raise seg4 to ~9 (above min) while seg5 is still ~7 (below min); because the min-crossing check
        // needs the PRIOR segment <= Min, that residual dip is never detected and is left below the reserve.
        // Diagnostic: charges=[13,12,11,10,9,7,9,11,13,15]. (Edge case — real usage/segment is far below the
        // charge rate, so this shouldn't occur in production; it exposes the same detection fragility.)
        var segs = new List<EnergySegment>
        {
            Seg(0, 12, buy: 10),
            Seg(1, 10, buy: 10),
            Seg(2,  8, buy: 10),
            Seg(3,  6, buy: 10),
            Seg(4,  4, buy: 10),
            Seg(5,  2, buy: 10),
            Seg(6,  4, buy: 10),
            Seg(7,  6, buy: 10),
            Seg(8,  8, buy: 10),
            Seg(9, 10, buy: 10),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        Assert.True(segs.All(s => s.EstimatedBatteryChargeKwh >= Cfg().MinCapacity - 0.01m),
            $"projected charge left below MinCapacity. actions=[{actions}] charges=[{charges}]");
    }
}
