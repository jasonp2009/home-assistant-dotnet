using System.Text.Json.Serialization;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TariffInformationPeriod
{
    OffPeak = 0,
    Shoulder = 1,
    SolarSponge = 2,
    Peak = 3
}