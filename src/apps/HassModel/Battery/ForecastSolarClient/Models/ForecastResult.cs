using System.Collections.Generic;

namespace src.apps.HassModel.Battery.ForecastSolarClient.Models;

public class ForecastResult
{
    public Dictionary<string, int> Result { get; set; }
}