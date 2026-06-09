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
    
    public static decimal GetWeightedPrice(this BaseInterval interval, decimal advancedPriceWeight)
    {
        var advancedPrice = interval.GetAdvancedPrice();
        if (advancedPrice is not null)
        {
            return advancedPrice.Predicted * (1 - advancedPriceWeight) +
                   (interval.ChannelType is ChannelType.FeedIn ? advancedPrice.Low : advancedPrice.High) * advancedPriceWeight;
        }
        if (!interval.IsEstimate())
        {
            return interval.GetPrice();
        }
        if (interval.ChannelType is ChannelType.FeedIn)
        {
            return interval.GetPrice() * (1 - advancedPriceWeight / 2);
        }
        return interval.GetPrice() * (1 + advancedPriceWeight);
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