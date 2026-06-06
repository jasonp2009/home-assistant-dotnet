using System.Text.Json.Serialization;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntervalDuration
{
    _5 = 5,

    _15 = 15,

    _30 = 30,
}