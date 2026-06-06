using System.Text.Json.Serialization;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PriceDescriptor
{
    Negative = 0,
    ExtremelyLow = 1,
    VeryLow = 2,
    Low = 3,
    Neutral = 4,
    High = 5,
    Spike = 6
}