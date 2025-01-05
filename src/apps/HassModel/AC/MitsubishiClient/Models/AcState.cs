using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using src.apps.Helpers;

namespace src.apps.HassModel.AC.MitsubishiClient.Models;

public class AcState
{
    [JsonConverter(typeof(BoolConverter))] public bool Power { get; set; }

    [JsonConverter(typeof(BoolConverter))] public bool Standby { get; set; }

    public AcMode SetMode { get; set; }

    [JsonConverter(typeof(BoolConverter))] public bool AutoMode { get; set; }

    public AcFanMode SetFan { get; set; }
    public int SetTemp { get; set; }
    public int RoomTemp { get; set; }
    public IEnumerable<AcZone> Zones { get; set; }

    public bool IsZoneOn(int zoneId)
    {
        return Zones.First(zone => zone.ZoneId == zoneId).Status == AcZoneStatus.On;
    }

    public bool IsAnyZoneOn()
    {
        return Zones.Any(zone => IsZoneOn(zone.ZoneId));
    }
}