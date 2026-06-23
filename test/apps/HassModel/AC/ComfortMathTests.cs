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
}
