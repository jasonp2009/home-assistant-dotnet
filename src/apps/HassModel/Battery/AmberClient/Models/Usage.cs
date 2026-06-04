using src.apps.HassModel.Battery.AmberClient.Enums;

namespace src.apps.HassModel.Battery.AmberClient.Models;

public class Usage : BaseInterval
{
    public string ChannelIdentifier { get; set; }
    public double Kwh { get; set; }
    public UsageQuality Quality { get; set; }
    public double Cost { get; set; }
}