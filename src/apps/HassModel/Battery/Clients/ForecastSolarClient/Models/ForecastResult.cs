using System.Collections.Generic;

namespace src.apps.HassModel.Battery.Clients.ForecastSolarClient.Models;

public class ForecastResult
{
    public Dictionary<string, int> Result { get; set; }
}