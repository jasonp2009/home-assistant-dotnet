namespace src.apps.HassModel.Battery.Models;

public class EnergySegment
{
    public required TimeSpan Duration { get; set; }
    public required DateTime StartUtc { get; set; }
    public DateTime EndUtc => StartUtc.Add(Duration);
    public required decimal EstimatedBatteryChargeKwh { get; set; }
    public decimal SolarForecastKwh { get; set; }
    public decimal? BuyPricePerKw { get; set; }
    public decimal? SellPricePerKw { get; set; }
    public bool IsChargeFromGrid { get; set; } = false;
}