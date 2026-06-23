namespace src.apps.HassModel.Battery.Models;

/// <summary>
/// A snapshot of the cumulative (lifetime) energy counters at a point in time, in kWh. Consumption
/// over an interval is the delta between two readings:
/// <c>ΔGridIn − ΔGridOut + ΔSolar − (ΔBatteryCharge − ΔBatteryDischarge)</c>. The battery
/// charge/discharge counters reset daily, so a backwards delta on either marks a reset (see
/// <see cref="Usage.UsageMath.ComputeConsumption"/>).
/// </summary>
public record CounterReading(
    DateTime TimestampUtc,
    decimal GridInKwh,
    decimal GridOutKwh,
    decimal SolarKwh,
    decimal BatteryChargeKwh,
    decimal BatteryDischargeKwh);
