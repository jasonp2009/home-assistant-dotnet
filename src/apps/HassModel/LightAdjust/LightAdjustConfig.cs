using System.Collections.Generic;
using HomeAssistantGenerated;

namespace src.apps.HassModel.LightAdjust;

public class LightAdjustConfig
{
    public IEnumerable<LightAdjustmentGroupConfig> AdjustmentGroups { get; set; }
}

public class LightAdjustmentGroupConfig
{
    public string Name { get; set; }
    public IEnumerable<LightEntity> Lights { get; set; }
    public IEnumerable<AdjustmentConfig> Adjustments { get; set; }
}

public class AdjustmentConfig
{
    public TimeOnly Time { get; set; }
    public double Transition { get; set; }
    public double Kelvin { get; set; }
    public double BrightnessPct { get; set; }
}