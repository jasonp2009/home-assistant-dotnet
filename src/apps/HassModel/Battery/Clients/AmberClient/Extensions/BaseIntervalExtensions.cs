using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery.Clients.AmberClient.Enums;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Extensions;

public static class BaseIntervalExtensions
{
    public static decimal GetPrice(this BaseInterval interval)
    {
        return interval.GetAdvancedPrice()?.Predicted ?? interval.PerKwh;
    }

    public static AdvancedPrice? GetAdvancedPrice(this BaseInterval interval)
    {
        return interval switch
        {
            CurrentInterval currentInterval => currentInterval.AdvancedPrice,
            ForecastInterval forecastInterval => forecastInterval.AdvancedPrice,
            _ => null
        };
    }
    
    public static bool IsEstimate(this BaseInterval interval)
    {
        return interval switch
        {
            CurrentInterval currentInterval => currentInterval.Estimate,
            _ => true
        };
    }
    
    public static bool IsEstimate(this IEnumerable<BaseInterval> intervals)
    {
        return intervals.OfType<CurrentInterval>().Any(interval => interval.Estimate);
    }
}