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
    /// Water-vapour pressure (hPa) at <paramref name="tempC"/> and <paramref name="relHumidityPct"/>
    /// via the Magnus approximation <c>rh/100 · 6.105 · exp(17.27·T / (237.7 + T))</c>. This is the
    /// humidity quantity in the Steadman apparent-temperature model.
    /// </summary>
    public static decimal VapourPressure(decimal tempC, decimal relHumidityPct)
    {
        var t = (double)tempC;
        var e = (double)relHumidityPct / 100.0 * 6.105 * Math.Exp(17.27 * t / (237.7 + t));
        return (decimal)e;
    }

    /// <summary>
    /// Humidity ("mugginess") offset in °C, from the Steadman vapour-pressure term
    /// <c>coefficient · (e(T, rh) − e(T, refRh))</c>, measured relative to
    /// <paramref name="referenceHumidityPct"/>. Positive when the air is more humid than the
    /// reference (sweat evaporates less → feels hotter), slightly negative when drier. Anchoring to a
    /// reference humidity means a typical indoor humidity contributes ~0, so only unusual humidity
    /// (e.g. a hot, muggy summer afternoon) shifts the felt temperature.
    /// </summary>
    public static decimal HumidityOffset(decimal tempC, decimal relHumidityPct, decimal referenceHumidityPct, decimal coefficient)
        => coefficient * (VapourPressure(tempC, relHumidityPct) - VapourPressure(tempC, referenceHumidityPct));

    /// <summary>
    /// Estimated felt temperature (°C): the air temperature plus the radiant envelope offset and,
    /// when <paramref name="relHumidityPct"/> is supplied, the humidity offset. The combined offset
    /// is clamped to ±<paramref name="maxOffset"/> so an extreme or glitched reading cannot drive the
    /// unit to extremes.
    /// </summary>
    public static decimal FeltTemperature(
        decimal airTemp,
        decimal outdoorTemp,
        decimal kEnv,
        decimal maxOffset,
        decimal? relHumidityPct = null,
        decimal referenceHumidityPct = 50M,
        decimal humidityCoefficient = 0.33M)
    {
        var offset = EnvelopeOffset(airTemp, outdoorTemp, kEnv);
        if (relHumidityPct is not null)
            offset += HumidityOffset(airTemp, relHumidityPct.Value, referenceHumidityPct, humidityCoefficient);
        offset = Math.Clamp(offset, -maxOffset, maxOffset);
        return airTemp + offset;
    }
}
