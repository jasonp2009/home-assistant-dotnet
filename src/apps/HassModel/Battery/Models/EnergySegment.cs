using System.Text;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Enums;

namespace src.apps.HassModel.Battery.Models;

public class EnergySegment
{
    public required TimeSpan Duration { get; set; }
    public required DateTime StartUtc { get; set; }
    public DateTime EndUtc => StartUtc.Add(Duration);
    public required decimal EstimatedBatteryChargeKwh { get; set; }
    public decimal SolarForecastKwh { get; set; }
    public bool IsDemandWindow { get; set; }
    public decimal? BuyPricePerKw { get; set; }
    public bool IsBuyEstimate { get; set; } = true;
    public AdvancedPrice? AdvancedBuyPrice { get; set; }
    public decimal? SellPricePerKw { get; set; }
    public bool IsSellEstimate { get; set; } = true;
    public AdvancedPrice? AdvancedSellPrice { get; set; }
    public EnergySegmentAction Action { get; set; } = EnergySegmentAction.None;

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"{StartUtc.ToLocalTime().ToShortTimeString()}");
        sb.Append($" {EstimatedBatteryChargeKwh}");
        sb.Append($" {Action.ToString()}");
        if (Action is EnergySegmentAction.Buy)
        {
            sb.Append($" {BuyPricePerKw}");
        }
        if (Action is EnergySegmentAction.Sell)
        {
            sb.Append($" {SellPricePerKw}");
        }
        return sb.ToString();
    }
}