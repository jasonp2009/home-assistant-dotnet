using System;
using System.Collections.Generic;
using System.Linq;

namespace src.apps.HassModel.AC;

/// <summary>
/// Pure maths for the unit's <em>driving setpoint</em> (no Home Assistant or IO, so it can be unit
/// tested). <see cref="AcControl.ShouldEnableZone"/> decides <em>whether</em> a zone runs; this
/// decides <em>how hard</em> the unit pushes, as an offset applied to the unit's own return-air
/// temperature: <c>setpoint = returnAirTemp ± drive</c>.
///
/// <para>A <b>negative</b> drive commands a setpoint past the unit's own return-air reading, which
/// idles the compressor while the fan keeps running — that is the deliberate <b>coil-residual
/// coast</b>: it blows the heat (or cold) still stored in the coil into the house instead of
/// stranding it. The saving is real, but only at the <em>end</em> of a cycle. Residual harvested
/// mid-cycle, while the room is still well short of its target, is simply re-heated minutes later.
/// So a negative drive is allowed only once the room is within <c>coastWindow</c> of the point where
/// its zone would switch off.</para>
/// </summary>
public static class DriveMath
{
    /// <summary>
    /// One room's contribution to the drive.
    /// <paramref name="MinutesSinceProgress"/> is the time since the room last made meaningful
    /// progress in the conditioned direction (or since its zone opened, whichever is later).
    /// </summary>
    public readonly record struct RoomDrive(
        decimal FeltTemp,
        decimal OffPoint,
        double MinutesSinceProgress);

    /// <summary>
    /// The original "time since progress" term: <c>minutes / 5 − 1</c>. It ramps up by one degree per
    /// five minutes a room fails to respond, and sits at −1 immediately after progress — which is what
    /// requests the coil-residual coast. Never returns less than −1.
    /// </summary>
    public static decimal StallTerm(double minutesSinceProgress)
        => Math.Max((decimal)(minutesSinceProgress / 5.0) - 1M, -1M);

    /// <summary>
    /// How far a room still is from the point at which its zone would switch off. Positive means the
    /// room still wants conditioning; zero or negative means it is already satisfied.
    /// </summary>
    public static decimal Error(decimal feltTemp, decimal offPoint, bool isCooling)
        => isCooling ? feltTemp - offPoint : offPoint - feltTemp;

    /// <summary>
    /// One room's requested drive: a proportional term on how far the room still has to go, plus the
    /// stall term.
    ///
    /// <para>Which way the stall term is allowed to act is the whole point. Once the room is within
    /// <paramref name="coastWindow"/> of its off-point the cycle is ending, so the stall term applies
    /// in full and may pull the drive negative — that is the coil-residual coast. Further out, it is
    /// clamped to a <em>bonus</em>: it can still add drive to a room that is failing to respond, but it
    /// can no longer subtract, so the unit is never told to stop while a room is still well short.</para>
    /// </summary>
    public static decimal ForRoom(RoomDrive room, bool isCooling, decimal coastWindow, decimal errorGain)
    {
        var error = Error(room.FeltTemp, room.OffPoint, isCooling);
        var stall = StallTerm(room.MinutesSinceProgress);
        return error > coastWindow
            ? errorGain * error + Math.Max(stall, 0M)
            : errorGain * Math.Max(error, 0M) + stall;
    }

    /// <summary>
    /// The drive for the whole unit, before capping.
    ///
    /// <para>Aggregated with <b>Max</b>, not an average: the unit has a single setpoint and the zones
    /// gate delivery, so a room that is already satisfied has its zone shut anyway and must not be
    /// able to dilute the demand of a room that is still cold.</para>
    ///
    /// <para>With no rooms to condition there is nothing to drive, so this returns −1 and lets the
    /// unit coast.</para>
    /// </summary>
    public static decimal RawForUnit(
        IReadOnlyCollection<RoomDrive> rooms, bool isCooling, decimal coastWindow, decimal errorGain)
        => rooms.Count == 0 ? -1M : rooms.Max(room => ForRoom(room, isCooling, coastWindow, errorGain));

    /// <summary>
    /// <see cref="RawForUnit"/> capped at <paramref name="maxDrive"/>. Deliberately <b>not</b> rounded:
    /// <c>MitsubishiClient.SetTemperature</c> already integerises the final setpoint, and does so in the
    /// conditioning direction (ceiling when heating, floor when cooling). Rounding here as well would
    /// round the wrong way first and throw away up to a degree of drive before the client ever sees it.
    /// The raw value is still worth logging separately, so it is visible when this cap binds.
    /// </summary>
    public static decimal ForUnit(
        IReadOnlyCollection<RoomDrive> rooms, bool isCooling, decimal coastWindow, decimal errorGain, decimal maxDrive)
        => Math.Clamp(RawForUnit(rooms, isCooling, coastWindow, errorGain), -1M, maxDrive);

    /// <summary>
    /// Folds one temperature reading into a running total of <em>net</em> progress in the conditioned
    /// direction, so a single sensor tick cannot be mistaken for the room responding. Movement the
    /// right way adds, movement the wrong way subtracts, and the total is floored at zero — a room
    /// oscillating on sensor quantisation therefore never accumulates progress. Returns the new total
    /// and whether it has reached <paramref name="threshold"/> (at which point the caller should reset
    /// the stall clock and zero the total).
    /// </summary>
    public static (decimal Progress, bool Reached) AccumulateProgress(
        decimal progressSoFar, decimal temperatureDelta, bool isCooling, decimal threshold)
    {
        var helpful = isCooling ? -temperatureDelta : temperatureDelta;
        var progress = Math.Max(progressSoFar + helpful, 0M);
        return progress >= threshold ? (0M, true) : (progress, false);
    }
}
