namespace src.apps.HassModel.Battery.AmberClient.Models;

public class CurrentInterval : BaseInterval
{
    public Range Range { get; set; }
    public bool Estimate { get; set; }
    public AdvancedPrice? AdvancedPrice { get; set; }
}