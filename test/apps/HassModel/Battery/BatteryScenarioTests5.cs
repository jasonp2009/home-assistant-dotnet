using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

public class BatteryScenarioTests5
{
    [Fact]
    public void EndToEnd_BuildSegments_ChargesCheapNow()
    {
        var prices = new List<BaseInterval>();
        for (int i = 0; i < 8; i++)
            prices.Add(GeneralInterval(i, i < 4 ? 5m : 40m));

        var segs = BatteryPlanner.BuildSegments(Base, currentChargeKwh: 11m, averageSegmentUsage: 1m,
            solarForecast: null, prices, Cfg());
        BatteryPlanner.OptimiseSegments(segs, Cfg(), 12m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var message = $"actions=[{actions}] charges=[{charges}]";

        Assert.True(segs[0].Action == EnergySegmentAction.Buy && segs[0].BuyPricePerKw == 5m, message);
    }

    [Fact]
    [Trait("Category", "KnownIssue")]
    public void Overcharge_DischargesEvenWithNoFeedInData()
    {
        var segs = new List<EnergySegment> { Seg(0, 50), Seg(1, 51), Seg(2, 52) };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 2)));
        var message = $"actions=[{actions}] charges=[{charges}]";

        Assert.False(segs.Any(s => s.Action == EnergySegmentAction.Sell), message);
    }
}
