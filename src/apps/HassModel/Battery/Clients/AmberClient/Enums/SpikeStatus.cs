using System.Text.Json.Serialization;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpikeStatus
{
    None = 0,
    Potential = 1,
    Spike = 2,
}