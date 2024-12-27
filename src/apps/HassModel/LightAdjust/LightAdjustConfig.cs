using System.Collections.Generic;
using HomeAssistantGenerated;

namespace src.apps.HassModel.LightAdjust;

public class LightAdjustConfig
{
    public IEnumerable<LightConfig> Lights { get; set; }
}

public class LightConfig
{
    public LightEntity LightEntity { get; set; }
    public IEnumerable<AdjustmentConfig> Adjustments { get; set; }
}

public class AdjustmentConfig
{
    public TimeOnly Time { get; set; }
    public double Transition { get; set; }
    public double Kelvin { get; set; }
    public double BrightnessPct { get; set; }
}