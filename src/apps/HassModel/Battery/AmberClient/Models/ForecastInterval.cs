namespace src.apps.HassModel.Battery.AmberClient.Models;

public partial class ForecastInterval : BaseInterval
{
    public Range Range { get; set; }
    public AdvancedPrice? AdvancedPrice { get; set; }
}