using src.apps.HassModel.Battery.Clients.AmberClient.Models;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Extensions;

public static class BaseIntervalExtensions
{
    public static decimal GetPrice(this BaseInterval interval)
    {
        return interval switch
        {
            CurrentInterval currentInterval => currentInterval.AdvancedPrice?.Predicted ?? currentInterval.PerKwh,
            ForecastInterval forecastInterval => forecastInterval.AdvancedPrice?.Predicted ?? forecastInterval.PerKwh,
            _ => interval.PerKwh
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
}