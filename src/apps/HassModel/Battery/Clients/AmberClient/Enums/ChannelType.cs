using System.Text.Json.Serialization;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelType
{
    General = 0,
    ControlledLoad = 1,
    FeedIn = 2,
}