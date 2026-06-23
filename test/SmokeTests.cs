using src.apps.HassModel.Battery.Enums;
using Xunit;

namespace Tests;

/// <summary>
/// Proves the test harness runs and can compile/reference types from the `src` app assembly.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void TestHarness_CanReferenceSrcAssembly()
    {
        Assert.Equal(0, (int)EnergySegmentAction.None);
        Assert.Equal(1, (int)EnergySegmentAction.Buy);
        Assert.Equal(2, (int)EnergySegmentAction.Sell);
    }
}
