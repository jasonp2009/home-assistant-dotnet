using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

public class BatteryScenarioTests4
{
    private const decimal Usage = 1m;

    [Fact]
    public void StartsBelowMin_ChargesNow()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 7,  buy: 10),
            Seg(1, 6,  buy: 10),
            Seg(2, 7,  buy: 10),
            Seg(3, 8,  buy: 10),
            Seg(4, 9,  buy: 10),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions  = string.Join(",", segs.Select(s => s.Action));
        var charges  = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var message  = $"actions=[{actions}] charges=[{charges}]";

        Assert.True(segs[0].Action == EnergySegmentAction.Buy, message);
    }

    [Fact]
    [Trait("Category", "KnownIssue")]
    public void FutureOvercharge_DoesNotDischargeFarFromFullSegment()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 40, sell: 30),
            Seg(1, 40, sell:  5),
            Seg(2, 40, sell:  5),
            Seg(3, 49, sell:  5),
            Seg(4, 50, sell:  5),
            Seg(5, 51, sell:  5),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions  = string.Join(",", segs.Select(s => s.Action));
        var charges  = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var message  = $"actions=[{actions}] charges=[{charges}]";

        Assert.False(segs[0].Action == EnergySegmentAction.Sell, message);
    }

    [Fact]
    public void Pessimism_PrefersTighterBandEstimate()
    {
        var segs = new List<EnergySegment>
        {
            Seg(0, 9,  buy: 12, buyLocked: false, advBuy: Adv(11, 12, 13)),
            Seg(1, 8,  buy: 11, buyLocked: false, advBuy: Adv(2,  11, 20)),
            Seg(2, 7,  buy: 50),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), Usage);

        var actions  = string.Join(",", segs.Select(s => s.Action));
        var charges  = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var message  = $"actions=[{actions}] charges=[{charges}]";

        Assert.True(segs[0].Action == EnergySegmentAction.Buy, message);
    }
}
