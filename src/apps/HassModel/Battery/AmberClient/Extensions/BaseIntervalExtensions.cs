using src.apps.HassModel.Battery.AmberClient.Models;

namespace src.apps.HassModel.Battery.AmberClient.Extensions;

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
}