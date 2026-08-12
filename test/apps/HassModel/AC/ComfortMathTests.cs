using System;
using System.Collections.Generic;
using src.apps.HassModel.AC;
using Xunit;

namespace Tests.apps.HassModel.AC;

/// <summary>
/// Unit tests for the pure felt-temperature maths: the radiant "envelope" offset that biases a room's
/// felt temperature toward the outdoor temperature, and the clamped felt-temperature estimate.
/// </summary>
public class ComfortMathTests
{
    // ---- EnvelopeOffset ----------------------------------------------------------------------

    [Fact]
    public void EnvelopeOffset_ColderOutside_IsNegative_FeelsColder()
    {
        // air 21, outside 8, k 0.1 -> 0.1 * (8 - 21) = -1.3
        Assert.Equal(-1.3m, ComfortMath.EnvelopeOffset(airTemp: 21m, outdoorTemp: 8m, kEnv: 0.1m));
    }

    [Fact]
    public void EnvelopeOffset_HotterOutside_IsPositive_FeelsHotter()
    {
        // air 25, outside 36, k 0.12 -> 0.12 * (36 - 25) = 1.32
        Assert.Equal(1.32m, ComfortMath.EnvelopeOffset(airTemp: 25m, outdoorTemp: 36m, kEnv: 0.12m));
    }

    [Fact]
    public void EnvelopeOffset_ZeroCoefficient_NoOffset()
    {
        Assert.Equal(0m, ComfortMath.EnvelopeOffset(airTemp: 22m, outdoorTemp: -5m, kEnv: 0m));
    }

    // ---- FeltTemperature ---------------------------------------------------------------------

    [Fact]
    public void FeltTemperature_Winter_BelowAirTemp()
    {
        // 21 + (-1.3) = 19.7
        Assert.Equal(19.7m, ComfortMath.FeltTemperature(airTemp: 21m, outdoorTemp: 8m, kEnv: 0.1m, maxOffset: 3m));
    }

    [Fact]
    public void FeltTemperature_Summer_AboveAirTemp()
    {
        // 25 + 1.32 = 26.32
        Assert.Equal(26.32m, ComfortMath.FeltTemperature(airTemp: 25m, outdoorTemp: 36m, kEnv: 0.12m, maxOffset: 3m));
    }

    [Fact]
    public void FeltTemperature_InternalRoom_EqualsAirTemp()
    {
        Assert.Equal(22m, ComfortMath.FeltTemperature(airTemp: 22m, outdoorTemp: 5m, kEnv: 0m, maxOffset: 3m));
    }

    [Fact]
    public void FeltTemperature_ClampsLargeNegativeOffset()
    {
        // raw offset 0.5 * (-20 - 22) = -21, clamped to -3 -> 19
        Assert.Equal(19m, ComfortMath.FeltTemperature(airTemp: 22m, outdoorTemp: -20m, kEnv: 0.5m, maxOffset: 3m));
    }

    [Fact]
    public void FeltTemperature_ClampsLargePositiveOffset()
    {
        // raw offset 0.5 * (50 - 22) = 14, clamped to 3 -> 25
        Assert.Equal(25m, ComfortMath.FeltTemperature(airTemp: 22m, outdoorTemp: 50m, kEnv: 0.5m, maxOffset: 3m));
    }

    // ---- HumidityOffset ----------------------------------------------------------------------

    [Fact]
    public void HumidityOffset_AtReferenceHumidity_IsZero()
    {
        Assert.Equal(0m, ComfortMath.HumidityOffset(tempC: 25m, relHumidityPct: 50m, referenceHumidityPct: 50m, coefficient: 0.33m));
    }

    [Fact]
    public void HumidityOffset_MoreHumidThanReference_FeelsHotter()
    {
        Assert.True(ComfortMath.HumidityOffset(tempC: 30m, relHumidityPct: 70m, referenceHumidityPct: 50m, coefficient: 0.33m) > 0m);
    }

    [Fact]
    public void HumidityOffset_DrierThanReference_FeelsCooler()
    {
        Assert.True(ComfortMath.HumidityOffset(tempC: 30m, relHumidityPct: 30m, referenceHumidityPct: 50m, coefficient: 0.33m) < 0m);
    }

    [Fact]
    public void HumidityOffset_HotMuggyAfternoon_IsAFewDegrees()
    {
        // 30 C, 70% vs 50% ref: ~0.33 * (29.6 - 21.1) hPa ~= +2.8 C
        var offset = ComfortMath.HumidityOffset(tempC: 30m, relHumidityPct: 70m, referenceHumidityPct: 50m, coefficient: 0.33m);
        Assert.InRange(offset, 2.4m, 3.2m);
    }

    // ---- HumidityOffset: calibration against Fanger PMV ---------------------------------------
    //
    // The configured HumidityCoefficient (0.10) is calibrated so the vapour-pressure term reproduces
    // the humidity sensitivity of Fanger PMV (ISO 7730, sedentary met 1.1, still air, t_r = t_a),
    // expressed as the equivalent air-temperature shift per +10 % RH. These tests pin that calibration
    // so it cannot drift silently; PMV reference values are 0.26 °C at 22 °C and 0.40 °C at 30 °C.

    private const decimal CalibratedHumidityCoefficient = 0.10m;

    private static decimal ShiftPer10PctRh(decimal tempC) =>
        ComfortMath.HumidityOffset(tempC, 55m, 50m, CalibratedHumidityCoefficient)
        - ComfortMath.HumidityOffset(tempC, 45m, 50m, CalibratedHumidityCoefficient);

    [Fact]
    public void HumidityOffset_AtRoomTemperature_MatchesPmvSensitivity()
    {
        // PMV says +10 % RH at 22 C is worth ~0.26 C of air temperature.
        Assert.InRange(ShiftPer10PctRh(22m), 0.23m, 0.29m);
    }

    [Fact]
    public void HumidityOffset_AtSummerTemperature_MatchesPmvSensitivity()
    {
        // PMV says +10 % RH at 30 C is worth ~0.40 C — humidity matters more when it is hot.
        Assert.InRange(ShiftPer10PctRh(30m), 0.37m, 0.45m);
    }

    [Fact]
    public void HumidityOffset_StillMattersAtWinterTemperatures()
    {
        // Deliberately NOT tapered to zero in winter: at 20 C a 10 % RH change is still worth ~0.24 C,
        // so the controller must keep compensating for it. Keeping the term is what makes comfort
        // humidity-invariant; removing it would let humidity swings pass through to how the room feels.
        Assert.InRange(ShiftPer10PctRh(20m), 0.20m, 0.26m);
    }

    [Fact]
    public void HumidityOffset_SensitivityGrowsWithTemperature()
    {
        // The Magnus form's temperature dependence is what matches PMV; it must stay monotonic.
        Assert.True(ShiftPer10PctRh(16m) < ShiftPer10PctRh(22m));
        Assert.True(ShiftPer10PctRh(22m) < ShiftPer10PctRh(30m));
    }

    // ---- FeltTemperature with humidity -------------------------------------------------------

    [Fact]
    public void FeltTemperature_HumidityAtReference_MatchesEnvelopeOnly()
    {
        var envelopeOnly = ComfortMath.FeltTemperature(airTemp: 22m, outdoorTemp: 14m, kEnv: 0.1m, maxOffset: 3m);
        var withRefHumidity = ComfortMath.FeltTemperature(
            airTemp: 22m, outdoorTemp: 14m, kEnv: 0.1m, maxOffset: 3m,
            relHumidityPct: 50m, referenceHumidityPct: 50m, humidityCoefficient: 0.33m);
        Assert.Equal(envelopeOnly, withRefHumidity);
    }

    [Fact]
    public void FeltTemperature_NullHumidity_MatchesEnvelopeOnly()
    {
        var envelopeOnly = ComfortMath.FeltTemperature(airTemp: 22m, outdoorTemp: 14m, kEnv: 0.1m, maxOffset: 3m);
        var withNullHumidity = ComfortMath.FeltTemperature(
            airTemp: 22m, outdoorTemp: 14m, kEnv: 0.1m, maxOffset: 3m, relHumidityPct: null);
        Assert.Equal(envelopeOnly, withNullHumidity);
    }

    [Fact]
    public void FeltTemperature_HotHumidSummer_FeelsHotterThanEnvelopeAlone()
    {
        // Same hot conditions; adding high humidity should raise the felt temperature.
        var envelopeOnly = ComfortMath.FeltTemperature(airTemp: 28m, outdoorTemp: 34m, kEnv: 0.1m, maxOffset: 5m);
        var humid = ComfortMath.FeltTemperature(
            airTemp: 28m, outdoorTemp: 34m, kEnv: 0.1m, maxOffset: 5m,
            relHumidityPct: 75m, referenceHumidityPct: 50m, humidityCoefficient: 0.33m);
        Assert.True(humid > envelopeOnly);
    }

    // ---- WindOffset --------------------------------------------------------------------------

    private const decimal WindCoefficient = 0.03m;
    private const decimal CalmWind = 10m;

    [Fact]
    public void WindOffset_ColdAndWindy_FeelsColder()
    {
        // 30 km/h is 20 above the calm threshold -> -0.6 C.
        Assert.Equal(-0.6m, ComfortMath.WindOffset(airTemp: 21m, outdoorTemp: 9m, windSpeedKmh: 30m, WindCoefficient, CalmWind));
    }

    [Fact]
    public void WindOffset_BelowCalmThreshold_IsZero()
    {
        // Ordinary background air movement is already priced into how a room normally feels.
        Assert.Equal(0m, ComfortMath.WindOffset(airTemp: 21m, outdoorTemp: 9m, windSpeedKmh: 8m, WindCoefficient, CalmWind));
    }

    [Fact]
    public void WindOffset_WarmerOutsideThanIn_IsZero()
    {
        // One-sided by design: air forced in from a warmer outdoors is not a cold draught, so wind must
        // never make a room feel cooler than the rest of the model already thinks it is.
        Assert.Equal(0m, ComfortMath.WindOffset(airTemp: 24m, outdoorTemp: 33m, windSpeedKmh: 40m, WindCoefficient, CalmWind));
    }

    [Fact]
    public void WindOffset_GrowsWithWindSpeed()
    {
        var breezy = ComfortMath.WindOffset(21m, 9m, 20m, WindCoefficient, CalmWind);
        var gale = ComfortMath.WindOffset(21m, 9m, 45m, WindCoefficient, CalmWind);
        Assert.True(gale < breezy);
    }

    [Fact]
    public void FeltTemperature_ColdAndWindy_IsBelowTheStillEquivalent()
    {
        var still = ComfortMath.FeltTemperature(
            airTemp: 21m, outdoorTemp: 9m, kEnv: 0.1m, maxOffset: 5m,
            windSpeedKmh: 5m, windCoefficient: WindCoefficient, calmWindKmh: CalmWind);
        var windy = ComfortMath.FeltTemperature(
            airTemp: 21m, outdoorTemp: 9m, kEnv: 0.1m, maxOffset: 5m,
            windSpeedKmh: 40m, windCoefficient: WindCoefficient, calmWindKmh: CalmWind);
        Assert.True(windy < still);
        Assert.Equal(-0.9m, windy - still); // 30 km/h of excess wind at 0.03
    }

    [Fact]
    public void FeltTemperature_NullWind_MatchesEnvelopeOnly()
    {
        var envelopeOnly = ComfortMath.FeltTemperature(airTemp: 21m, outdoorTemp: 9m, kEnv: 0.1m, maxOffset: 5m);
        var withNullWind = ComfortMath.FeltTemperature(
            airTemp: 21m, outdoorTemp: 9m, kEnv: 0.1m, maxOffset: 5m,
            windSpeedKmh: null, windCoefficient: WindCoefficient, calmWindKmh: CalmWind);
        Assert.Equal(envelopeOnly, withNullWind);
    }

    [Fact]
    public void FeltTemperature_ClampDoesNotBiteInRealisticWinterConditions()
    {
        // The clamp is a sanity guard against a glitched reading, not a tuning knob: if it binds in
        // ordinary weather it silently flattens the correction and hands the weather dependence back.
        // Harshest plausible local winter: 0 C outdoors, 21 C indoors, gale-force 50 km/h.
        var offset = ComfortMath.FeltTemperature(
            airTemp: 21m, outdoorTemp: 0m, kEnv: 0.1m, maxOffset: 5m,
            relHumidityPct: 35m, referenceHumidityPct: 50m, humidityCoefficient: 0.10m,
            windSpeedKmh: 50m, windCoefficient: WindCoefficient, calmWindKmh: CalmWind) - 21m;

        Assert.True(offset > -5m, $"combined offset {offset} reached the clamp in ordinary winter weather");
    }

    // ---- EmaStep -----------------------------------------------------------------------------

    private static readonly DateTime T0 = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmaStep_DisabledTimeConstant_ReturnsRawReading()
    {
        Assert.Equal(20m, ComfortMath.EmaStep(previousEma: 10m, previousUtc: T0, reading: 20m, nowUtc: T0.AddHours(5), timeConstantHours: 0m));
    }

    [Fact]
    public void EmaStep_NonPositiveElapsed_HoldsPreviousValue()
    {
        Assert.Equal(10m, ComfortMath.EmaStep(previousEma: 10m, previousUtc: T0, reading: 20m, nowUtc: T0, timeConstantHours: 15m));
    }

    [Fact]
    public void EmaStep_OneTimeConstantElapsed_MovesAbout63Percent()
    {
        // dt == tau -> alpha = 1 - e^-1 = 0.632; 10 + 0.632 * (20 - 10) = 16.32
        var ema = ComfortMath.EmaStep(previousEma: 10m, previousUtc: T0, reading: 20m, nowUtc: T0.AddHours(15), timeConstantHours: 15m);
        Assert.Equal(16.3m, ema, 1);
    }

    [Fact]
    public void EmaStep_ShortStepRelativeToTau_BarelyMoves()
    {
        // 1 h step with tau 15 h -> alpha ~= 0.065; a single hot reading barely shifts the average.
        var ema = ComfortMath.EmaStep(previousEma: 12m, previousUtc: T0, reading: 20m, nowUtc: T0.AddHours(1), timeConstantHours: 15m);
        Assert.InRange(ema, 12.4m, 12.7m);
    }

    // ---- SeedEma -----------------------------------------------------------------------------

    [Fact]
    public void SeedEma_EmptyHistory_ReturnsNull()
    {
        Assert.Null(ComfortMath.SeedEma(new List<(DateTime, decimal)>(), timeConstantHours: 15m));
    }

    [Fact]
    public void SeedEma_SingleSample_ReturnsThatSample()
    {
        var seed = ComfortMath.SeedEma(new List<(DateTime, decimal)> { (T0, 17m) }, timeConstantHours: 15m);
        Assert.NotNull(seed);
        Assert.Equal(17m, seed!.Value.Ema);
        Assert.Equal(T0, seed.Value.AsOfUtc);
    }

    [Fact]
    public void SeedEma_DiurnalSwing_SettlesNearTheDailyMean()
    {
        // 48 hourly samples oscillating +/-6 C around a mean of 12 C; the EMA should flatten close to
        // the mean, well inside the raw 6..18 range.
        var samples = new List<(DateTime, decimal)>();
        for (var h = 0; h < 48; h++)
            samples.Add((T0.AddHours(h), 12m + 6m * (decimal)Math.Sin(2 * Math.PI * h / 24.0)));

        var seed = ComfortMath.SeedEma(samples, timeConstantHours: 15m);
        Assert.NotNull(seed);
        Assert.InRange(seed!.Value.Ema, 10.5m, 13.5m);
        Assert.Equal(T0.AddHours(47), seed.Value.AsOfUtc);
    }
}
