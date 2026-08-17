using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Enums;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Unit tests for <see cref="BatteryPlanner.ApplyActionReversalCooldown"/> — the actuation-level guard that
/// stops the discretionary buy/sell thrash the audit found in production (Buy 25c -> Sell 24c -> Buy 26c on
/// consecutive 5-minute segments, 2026-08-06).
///
/// The rule is deliberately asymmetric, and each asymmetry is pinned below: it only ever downgrades to None
/// (never forces an action to continue), and it never blocks a floor-defence buy (refusing to charge can
/// strand the battery on the floor).
/// </summary>
public class BatteryReversalCooldownTests
{
    private static BatteryConfig CooldownCfg(int segments = 3)
    {
        var cfg = Cfg();
        cfg.ActionReversalCooldownSegments = segments;
        return cfg;
    }

    private static EnergySegmentAction Run(
        EnergySegmentAction proposed,
        EnergySegmentActionReason reason,
        EnergySegmentAction last,
        int segmentsSince,
        BatteryConfig? cfg = null)
        => BatteryPlanner.ApplyActionReversalCooldown(proposed, reason, last, segmentsSince, cfg ?? CooldownCfg());

    // -------------------------------------------------------------------------------------------
    // The behaviour the guard exists for
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The 2026-08-06 shape: an arbitrage sell arriving 2 segments after a buy. Selling at feed-in what was
    /// just imported at retail loses the whole spread, so the sell is suppressed.
    /// </summary>
    [Fact]
    public void SuppressesArbitrageSell_ArrivingSoonAfterABuy()
    {
        var result = Run(EnergySegmentAction.Sell, EnergySegmentActionReason.Arbitrage, EnergySegmentAction.Buy, segmentsSince: 2);
        Assert.Equal(EnergySegmentAction.None, result);
    }

    /// <summary>The mirror case: a discretionary (arbitrage) buy reversing a recent sell is also suppressed.</summary>
    [Fact]
    public void SuppressesArbitrageBuy_ArrivingSoonAfterASell()
    {
        var result = Run(EnergySegmentAction.Buy, EnergySegmentActionReason.Arbitrage, EnergySegmentAction.Sell, segmentsSince: 1);
        Assert.Equal(EnergySegmentAction.None, result);
    }

    /// <summary>A boundary-solver sell (battery at MaxCapacity) is discretionary enough to suppress too.</summary>
    [Fact]
    public void SuppressesUsageSell_ArrivingSoonAfterABuy()
    {
        var result = Run(EnergySegmentAction.Sell, EnergySegmentActionReason.Usage, EnergySegmentAction.Buy, segmentsSince: 1);
        Assert.Equal(EnergySegmentAction.None, result);
    }

    // -------------------------------------------------------------------------------------------
    // The safety asymmetries
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A floor-defence (Usage) BUY is never blocked, however recently we sold. Refusing to charge here would
    /// strand the battery on MinCapacity and force a dearer import later — the exact harm the audit found.
    /// This is the single most important assertion in the file: if it regresses, the guard can starve the
    /// battery.
    /// </summary>
    [Fact]
    public void NeverBlocksFloorDefenceBuy_EvenImmediatelyAfterASell()
    {
        var result = Run(EnergySegmentAction.Buy, EnergySegmentActionReason.Usage, EnergySegmentAction.Sell, segmentsSince: 0);
        Assert.Equal(EnergySegmentAction.Buy, result);
    }

    /// <summary>
    /// The guard only ever downgrades. A proposed None is never upgraded into a forced continuation, so it
    /// can never keep discharging a battery the planner has decided to stop discharging.
    /// </summary>
    [Fact]
    public void NeverForcesAnActionToContinue()
    {
        var result = Run(EnergySegmentAction.None, EnergySegmentActionReason.NotApplicable, EnergySegmentAction.Sell, segmentsSince: 0);
        Assert.Equal(EnergySegmentAction.None, result);
    }

    // -------------------------------------------------------------------------------------------
    // Boundaries
    // -------------------------------------------------------------------------------------------

    /// <summary>Continuing the same action is not a reversal — a sustained run must not be chopped up.</summary>
    [Fact]
    public void AllowsContinuationOfTheSameAction()
    {
        var result = Run(EnergySegmentAction.Sell, EnergySegmentActionReason.Arbitrage, EnergySegmentAction.Sell, segmentsSince: 1);
        Assert.Equal(EnergySegmentAction.Sell, result);
    }

    /// <summary>
    /// At exactly the cooldown length the reversal is allowed through — pins the boundary as inclusive, so
    /// a cooldown of 3 blocks segments 0,1,2 and permits 3.
    /// </summary>
    [Fact]
    public void AllowsReversalOnceTheCooldownHasElapsed()
    {
        Assert.Equal(EnergySegmentAction.None,
            Run(EnergySegmentAction.Sell, EnergySegmentActionReason.Arbitrage, EnergySegmentAction.Buy, segmentsSince: 2));
        Assert.Equal(EnergySegmentAction.Sell,
            Run(EnergySegmentAction.Sell, EnergySegmentActionReason.Arbitrage, EnergySegmentAction.Buy, segmentsSince: 3));
    }

    /// <summary>With no prior action (fresh process) nothing is suppressed.</summary>
    [Fact]
    public void NoPriorAction_AllowsAnything()
    {
        var result = Run(EnergySegmentAction.Sell, EnergySegmentActionReason.Arbitrage, EnergySegmentAction.None, segmentsSince: int.MaxValue);
        Assert.Equal(EnergySegmentAction.Sell, result);
    }

    /// <summary>Zero (the default for configs that don't set it) disables the guard entirely.</summary>
    [Fact]
    public void Disabled_WhenCooldownIsZero()
    {
        var result = Run(EnergySegmentAction.Sell, EnergySegmentActionReason.Arbitrage, EnergySegmentAction.Buy,
            segmentsSince: 0, cfg: CooldownCfg(0));
        Assert.Equal(EnergySegmentAction.Sell, result);
    }
}
