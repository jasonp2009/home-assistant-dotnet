using System.Text.Json.Serialization;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageQuality
{
    Estimated = 0,
    Billable = 1,
}