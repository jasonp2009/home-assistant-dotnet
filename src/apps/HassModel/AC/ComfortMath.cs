using System;

namespace src.apps.HassModel.AC;

/// <summary>
/// Pure comfort / felt-temperature math (no Home Assistant or IO), so it can be unit tested. The
/// controller regulates an estimated <em>felt</em> (apparent) temperature rather than the raw
/// dry-bulb air temperature a sensor reads, because perceived comfort depends on more than the air
/// temperature: chiefly the temperature of the surrounding surfaces (mean radiant temperature),
/// which drifts toward the outdoor temperature.
/// </summary>
public static class ComfortMath
{
    /// <summary>
    /// Radiant ("envelope") offset in °C: how much colder or warmer a room <em>feels</em> than its
    /// air temperature because the building's surfaces (external walls, windows) sit between the
    /// indoor air and the outdoor air. Returns <c>kEnv · (outdoorTemp − airTemp)</c> — negative when
    /// it is colder outside (cold surfaces draw body heat away → feels colder), positive when it is
    /// hotter outside (warm surfaces → feels hotter). <paramref name="kEnv"/> rolls the room's
    /// exposure (window area / insulation) into one coefficient; use 0 for a fully internal room.
    /// </summary>
    public static decimal EnvelopeOffset(decimal airTemp, decimal outdoorTemp, decimal kEnv)
        => kEnv * (outdoorTemp - airTemp);

    /// <summary>
    /// Estimated felt temperature (°C): the air temperature plus the radiant envelope offset, with
    /// the total offset clamped to ±<paramref name="maxOffset"/> so an extreme or glitched outdoor
    /// reading cannot drive the unit to extremes.
    /// </summary>
    public static decimal FeltTemperature(decimal airTemp, decimal outdoorTemp, decimal kEnv, decimal maxOffset)
    {
        var offset = Math.Clamp(EnvelopeOffset(airTemp, outdoorTemp, kEnv), -maxOffset, maxOffset);
        return airTemp + offset;
    }
}
