using System.Collections.Generic;
using HomeAssistantGenerated;

namespace src.apps.HassModel.AlarmLight;

public class AlarmLightConfig
{
    public IEnumerable<AlarmLightGroup> Groups { get; set; }
}

public class AlarmLightGroup
{
    public string Name { get; set; }
    public SensorEntity NextAlarm { get; set; }
    public IEnumerable<LightEntity> Lights { get; set; }
}