using System.Collections.Generic;
using src.apps.HassModel.AC;
using Xunit;

namespace Tests.apps.HassModel.AC;

/// <summary>
/// Unit tests for the driving-setpoint maths: how hard the unit is pushed past its own return-air
/// temperature, and in particular when it is allowed to coast on residual coil heat/cold.
/// </summary>
public class DriveMathTests
{
    private const decimal CoastWindow = 1m;
    private const decimal ErrorGain = 1m;
    private const decimal MaxDrive = 5m;

    private static DriveMath.RoomDrive Room(decimal felt, decimal offPoint, double stalledMinutes)
        => new(felt, offPoint, stalledMinutes);

    private static decimal Heating(DriveMath.RoomDrive room)
        => DriveMath.ForRoom(room, isCooling: false, CoastWindow, ErrorGain);

    // ---- StallTerm ---------------------------------------------------------------------------

    [Fact]
    public void StallTerm_JustAfterProgress_IsMinusOne()
        => Assert.Equal(-1m, DriveMath.StallTerm(0));

    [Fact]
    public void StallTerm_RampsOneDegreePerFiveMinutes()
    {
        Assert.Equal(0m, DriveMath.StallTerm(5));
        Assert.Equal(1m, DriveMath.StallTerm(10));
        Assert.Equal(3m, DriveMath.StallTerm(20));
    }

    [Fact]
    public void StallTerm_NeverGoesBelowMinusOne()
        => Assert.Equal(-1m, DriveMath.StallTerm(-30));

    // ---- Error -------------------------------------------------------------------------------

    [Fact]
    public void Error_Heating_IsPositiveWhileTheRoomIsBelowItsOffPoint()
        => Assert.Equal(1.8m, DriveMath.Error(feltTemp: 20.2m, offPoint: 22m, isCooling: false));

    [Fact]
    public void Error_Cooling_IsPositiveWhileTheRoomIsAboveItsOffPoint()
        => Assert.Equal(1.5m, DriveMath.Error(feltTemp: 25.5m, offPoint: 24m, isCooling: true));

    [Fact]
    public void Error_IsNegativeOnceSatisfied()
        => Assert.True(DriveMath.Error(feltTemp: 23m, offPoint: 22m, isCooling: false) < 0m);

    // ---- ForRoom: the coast is confined to the end of a cycle ---------------------------------

    [Fact]
    public void ForRoom_FarFromTarget_WillNotCoast_EvenRightAfterProgress()
    {
        // The measured production pathology: a room 1.8 C short of its off-point ticked up 0.1 C, which
        // reset the stall clock, and the unit was then told to sit a degree BELOW its own return air.
        // Outside the coast window the stall term may no longer subtract.
        var drive = Heating(Room(felt: 20.2m, offPoint: 22m, stalledMinutes: 0));
        Assert.Equal(1.8m, drive);
        Assert.True(drive > 0m, "a room well short of target must never command a negative drive");
    }

    [Fact]
    public void ForRoom_NearTarget_StillCoastsOnResidual()
    {
        // Within the coast window the cycle is ending, so the residual in the coil would otherwise be
        // stranded: the negative drive is preserved exactly as before.
        Assert.Equal(-0.5m, Heating(Room(felt: 21.5m, offPoint: 22m, stalledMinutes: 0)));
    }

    [Fact]
    public void ForRoom_AlreadySatisfied_Coasts()
        => Assert.Equal(-1m, Heating(Room(felt: 22.5m, offPoint: 22m, stalledMinutes: 0)));

    [Fact]
    public void ForRoom_FarFromTargetAndStalled_StallStillAddsDrive()
    {
        // 1.8 C short and no progress for 20 minutes -> proportional 1.8 plus a stall bonus of 3.
        Assert.Equal(4.8m, Heating(Room(felt: 20.2m, offPoint: 22m, stalledMinutes: 20)));
    }

    [Fact]
    public void ForRoom_DriveGrowsWithTheGapToTarget()
    {
        var nearly = Heating(Room(felt: 21m, offPoint: 22m, stalledMinutes: 0));
        var short_ = Heating(Room(felt: 20m, offPoint: 22m, stalledMinutes: 0));
        var cold = Heating(Room(felt: 18m, offPoint: 22m, stalledMinutes: 0));
        Assert.True(nearly < short_);
        Assert.True(short_ < cold);
    }

    [Fact]
    public void ForRoom_Cooling_MirrorsHeating()
    {
        var drive = DriveMath.ForRoom(Room(felt: 26m, offPoint: 24m, stalledMinutes: 0), isCooling: true, CoastWindow, ErrorGain);
        Assert.Equal(2m, drive);
    }

    // ---- ForUnit: aggregation ----------------------------------------------------------------

    [Fact]
    public void ForUnit_TakesTheNeediestRoom_NotTheAverage()
    {
        // One room is coasting at its off-point, another is 2 C short. Averaging (the old behaviour)
        // would have diluted the cold room to roughly nothing; Max must follow the cold room.
        var rooms = new List<DriveMath.RoomDrive>
        {
            Room(felt: 22.5m, offPoint: 22m, stalledMinutes: 0), // satisfied, wants to coast at -1
            Room(felt: 20m, offPoint: 22m, stalledMinutes: 0)    // 2 C short, wants +2
        };
        Assert.Equal(2m, DriveMath.ForUnit(rooms, isCooling: false, CoastWindow, ErrorGain, MaxDrive));
    }

    [Fact]
    public void ForUnit_NoRooms_Coasts()
        => Assert.Equal(-1m, DriveMath.ForUnit(new List<DriveMath.RoomDrive>(), isCooling: false, CoastWindow, ErrorGain, MaxDrive));

    [Fact]
    public void ForUnit_CapsAtMaxDrive()
    {
        var rooms = new List<DriveMath.RoomDrive> { Room(felt: 10m, offPoint: 22m, stalledMinutes: 60) };
        Assert.Equal(MaxDrive, DriveMath.ForUnit(rooms, isCooling: false, CoastWindow, ErrorGain, MaxDrive));
    }

    [Fact]
    public void ForUnit_NeverGoesBelowMinusOne()
    {
        var rooms = new List<DriveMath.RoomDrive> { Room(felt: 30m, offPoint: 22m, stalledMinutes: 0) };
        Assert.Equal(-1m, DriveMath.ForUnit(rooms, isCooling: false, CoastWindow, ErrorGain, MaxDrive));
    }

    [Fact]
    public void ForUnit_KeepsFractionalDrive_ForTheClientToRoundInTheConditioningDirection()
    {
        // MitsubishiClient.SetTemperature already integerises, and ceilings when heating, so 1.2 becomes
        // 2 C of drive. Flooring here first would have cut it back to 1 - the same double-rounding that
        // turned a 0.98 drive into a commanded 0.
        var rooms = new List<DriveMath.RoomDrive> { Room(felt: 20.8m, offPoint: 22m, stalledMinutes: 0) };
        Assert.Equal(1.2m, DriveMath.ForUnit(rooms, isCooling: false, CoastWindow, ErrorGain, MaxDrive));
    }

    // ---- AccumulateProgress: noise resistance -------------------------------------------------

    [Fact]
    public void AccumulateProgress_SingleSensorTick_DoesNotCountAsProgress()
    {
        var (progress, reached) = DriveMath.AccumulateProgress(0m, temperatureDelta: 0.1m, isCooling: false, threshold: 0.3m);
        Assert.False(reached);
        Assert.Equal(0.1m, progress);
    }

    [Fact]
    public void AccumulateProgress_SustainedRise_ReachesThresholdAndResets()
    {
        var progress = 0m;
        var reached = false;
        for (var i = 0; i < 3; i++)
            (progress, reached) = DriveMath.AccumulateProgress(progress, 0.1m, isCooling: false, threshold: 0.3m);

        Assert.True(reached);
        Assert.Equal(0m, progress); // zeroed ready for the next stretch
    }

    [Fact]
    public void AccumulateProgress_Oscillation_NeverAccumulates()
    {
        // A room jittering on sensor quantisation must never look like it is responding.
        var progress = 0m;
        for (var i = 0; i < 20; i++)
        {
            bool reached;
            (progress, reached) = DriveMath.AccumulateProgress(progress, 0.1m, isCooling: false, threshold: 0.3m);
            Assert.False(reached);
            (progress, reached) = DriveMath.AccumulateProgress(progress, -0.1m, isCooling: false, threshold: 0.3m);
            Assert.False(reached);
        }
    }

    [Fact]
    public void AccumulateProgress_FloorsAtZero_SoASlideDoesNotBankNegativeCredit()
    {
        var (progress, _) = DriveMath.AccumulateProgress(0m, temperatureDelta: -5m, isCooling: false, threshold: 0.3m);
        Assert.Equal(0m, progress);
    }

    [Fact]
    public void AccumulateProgress_Cooling_CountsFallingTemperature()
    {
        var (progress, reached) = DriveMath.AccumulateProgress(0m, temperatureDelta: -0.4m, isCooling: true, threshold: 0.3m);
        Assert.True(reached);
        Assert.Equal(0m, progress);

        var (_, wrongWay) = DriveMath.AccumulateProgress(0m, temperatureDelta: 0.4m, isCooling: true, threshold: 0.3m);
        Assert.False(wrongWay);
    }
}
