using System.Text.Json.Serialization;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TariffInformationSeason
{
    Default = 0,
    Summer = 1,
    Autumn = 2,
    Winter = 3,
    Spring = 4,
    NonSummer = 5,
    Holiday = 6,
    Weekend = 7,
    WeekendHoliday = 8,
    Weekday = 9
}