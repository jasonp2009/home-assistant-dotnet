using src.apps.HassModel.Battery.Clients.AmberClient.Enums;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Models;

public class TariffInformation
{
    public TariffInformationPeriod Period { get; set; }
    public TariffInformationSeason Season { get; set; }
    public double Block { get; set; }
    public bool DemandWindow { get; set; }
}