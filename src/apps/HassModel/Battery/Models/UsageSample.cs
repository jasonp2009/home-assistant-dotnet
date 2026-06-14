namespace src.apps.HassModel.Battery.Models;

/// <summary>
/// Measured household consumption (kWh) attributed to the 5-minute segment starting at
/// <paramref name="SegmentStartUtc"/>. Produced by spreading a solar-aligned measurement window
/// evenly across the segments it covers (see <see cref="Usage.UsageMath"/>).
/// </summary>
public record UsageSample(DateTime SegmentStartUtc, decimal ConsumptionKwh);
