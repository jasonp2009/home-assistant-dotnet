using System.Collections.Generic;
using HomeAssistantGenerated;

namespace src.apps.HassModel.LightSync;

public class LightSyncConfig
{
    public IEnumerable<ZoneConfig> Zones { get; set; }
}

public class ZoneConfig
{
    public string Name { get; set; }
    public LightEntity PrimaryLight { get; set; }
    public IEnumerable<SecondaryLightGroup> SecondaryLightGroups { get; set; }
}

public class SecondaryLightGroup
{
    public int OnAtPct { get; set; }
    public double OnAt => (double)OnAtPct / 100 * 255;
    public IEnumerable<LightEntity> Lights { get; set; }
}