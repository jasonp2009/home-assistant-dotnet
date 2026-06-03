namespace src.apps.HassModel.Battery;

public class EnergySegment
{
    public decimal BatteryChargeKwh { get; set; }
    public bool IsChargeFromGrid { get; set; }
}