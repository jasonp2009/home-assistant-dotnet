using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

public class BatteryScenarioTests9
{
    [Fact]
    [Trait("Category", "KnownIssue")]
    public void SharpPeakAndTrough_LeftBadlyOutOfBounds()
    {
        // The projection passes THROUGH a sharp peak (60 kWh, over max) and a sharp trough (1 kWh, under
        // min). Because boundary detection only flags a limit while the charge is "still moving past it",
        // a single corrective Sell de-triggers it: the 60 peak is no longer rising and the 1 trough no
        // longer falling, so CalculateBoundaryResult reports in-bounds and the loop exits — leaving BOTH a
        // ~59 over-charge and a ~1 under-charge unresolved. Diagnostic: one Sell at seg0, final charges
        // [39,49,59,49,39,19,9,1,9,19]. (Sharp single-segment swings are unrealistic for solar/usage, but
        // this shows the finding-#5 detection gap can leave the plan badly out of bounds, not just by 1 kWh.)
        var segs = new List<EnergySegment>
        {
            Seg(0, 40, buy: 10, sell: 20), Seg(1, 50, buy: 10, sell: 20), Seg(2, 60, buy: 10, sell: 20),
            Seg(3, 50, buy: 10, sell: 20), Seg(4, 40, buy: 10, sell: 20), Seg(5, 20, buy: 10, sell: 20),
            Seg(6, 10, buy: 10, sell: 20), Seg(7, 2, buy: 10, sell: 20), Seg(8, 10, buy: 10, sell: 20), Seg(9, 20, buy: 10, sell: 20),
        };

        BatteryPlanner.OptimiseSegments(segs, Cfg(), 1m);

        var actions = string.Join(",", segs.Select(s => s.Action));
        var charges = string.Join(",", segs.Select(s => Math.Round(s.EstimatedBatteryChargeKwh, 1)));
        Assert.True(
            segs.All(s => s.EstimatedBatteryChargeKwh >= Cfg().MinCapacity - 0.01m && s.EstimatedBatteryChargeKwh <= Cfg().MaxCapacity + 0.01m),
            $"plan left out of bounds. actions=[{actions}] charges=[{charges}]");
    }
}
