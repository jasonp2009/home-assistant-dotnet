using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using src.apps.HassModel.Battery.Clients.AmberClient;
using src.apps.HassModel.Battery.Clients.ForecastSolarClient;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Extensions;
using src.apps.HassModel.Battery.Models;

namespace src.apps.HassModel.Battery;

[NetDaemonApp]
public class BatteryControl
{
    private BatteryConfig _config;
    private ForecastSolarClient _forecastSolarClient;
    private AmberClient _amberClient;
    
    public BatteryControl(IHaContext ha, INetDaemonScheduler scheduler, IAppConfig<BatteryConfig> config,
        ILogger<BatteryControl> logger, ForecastSolarClient forecastSolarClient, AmberClient amberClient)
    {
        _config = config.Value;
        _forecastSolarClient = forecastSolarClient;
        _amberClient = amberClient;
        GetCurrentActionAsync().Wait();
    }

    private async Task<EnergySegmentAction> GetCurrentActionAsync()
    {
        var energySegments = await InitialiseEnergySegmentsAsync();
        var boundaryResult = CalculateBoundaryResult(energySegments);
        while (boundaryResult.IsOutOfBounds && energySegments.First().Action is EnergySegmentAction.None)
        {
            if (boundaryResult.IsMax == true)
            {
                var maxPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        index <= boundaryResult.IndexOfBoundaryCrossing)
                    .MaxBy(segment => segment.SellPricePerKw ?? decimal.MinValue);
                if (maxPriceSegment is null)
                    break;
                var maxPriceSegmentIndex = energySegments.IndexOf(maxPriceSegment);
                maxPriceSegment.Action = EnergySegmentAction.Sell;
                for (var i = maxPriceSegmentIndex; i <= boundaryResult.IndexOfBoundaryCrossing; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh -= _config.SegmentChargeAmountKwh;
                }
            }
            if (boundaryResult.IsMax == false)
            {
                var lowestPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        index <= boundaryResult.IndexOfBoundaryCrossing &&
                        !segment.IsDemandWindow)
                    .MinBy(segment => segment.BuyPricePerKw ?? decimal.MaxValue);
                if (lowestPriceSegment is null)
                    break;
                var lowestPriceSegmentIndex = energySegments.IndexOf(lowestPriceSegment);
                lowestPriceSegment.Action = EnergySegmentAction.Buy;
                for (var i = lowestPriceSegmentIndex; i <= boundaryResult.IndexOfBoundaryCrossing; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh += _config.SegmentChargeAmountKwh;
                }
            }
            boundaryResult = CalculateBoundaryResult(energySegments);
        }
        return energySegments.First().Action;
    }

    private async Task<List<EnergySegment>> InitialiseEnergySegmentsAsync()
    {
        var averageHalfHourUsage = GetAverageSegmentUsage();
        var currentBatteryChargeKwh = GetCurrentBatteryChargeKwh();
        currentBatteryChargeKwh = 30;
        var startUtc = GetCurrentSegmentStart();
        var solarForecastTask = _forecastSolarClient.GetForecastAsync();
        var amberPricesTask = _amberClient.GetCurrentPriceAsync();
        await Task.WhenAll(solarForecastTask, amberPricesTask);
        var solarForecast = solarForecastTask.Result;
        var amberPrices = amberPricesTask.Result;
        var curEnergySegment = new EnergySegment
        {
            EstimatedBatteryChargeKwh = currentBatteryChargeKwh,
            Duration = _config.SegmentSize,
            StartUtc = startUtc
        };
        curEnergySegment.ApplySolarForecast(solarForecast);
        curEnergySegment.ApplyPrice(amberPrices);
        var energySegments = new List<EnergySegment> {curEnergySegment};
        while (curEnergySegment.BuyPricePerKw is not null && curEnergySegment.SellPricePerKw is not null)
        {
            curEnergySegment = new EnergySegment
            {
                EstimatedBatteryChargeKwh = curEnergySegment.EstimatedBatteryChargeKwh - averageHalfHourUsage,
                Duration = _config.SegmentSize,
                StartUtc = curEnergySegment.StartUtc + _config.SegmentSize
            };
            curEnergySegment.ApplySolarForecast(solarForecast);
            curEnergySegment.ApplyPrice(amberPrices);
            energySegments.Add(curEnergySegment);
        }
        return energySegments;
    }

    private decimal GetAverageSegmentUsage()
    {
        var gridIn3Days = Convert.ToDecimal(_config.GridIn3DaysEntity.State);
        var gridOut3Days = Convert.ToDecimal(_config.GridOut3DaysEntity.State);
        var solarProduction3Days = Convert.ToDecimal(_config.SolarProduction3DaysEntity.State);
        var batteryChargeDiff3Days = Convert.ToDecimal(_config.BatteryChargeDiff3DaysEntity.State);
        var batteryUsage = ((batteryChargeDiff3Days / 100) * _config.BatteryCapacity);
        var usage3Days = gridIn3Days - gridOut3Days + solarProduction3Days - batteryUsage;
        var segmentsIn3Days = Convert.ToDecimal(TimeSpan.FromDays(3) / _config.SegmentSize);
        return usage3Days / segmentsIn3Days;
    }

    private decimal GetCurrentBatteryChargeKwh()
    {
        return (Convert.ToDecimal(_config.SolarBatteryStateOfChargeEntity.State) / 100) * _config.BatteryCapacity;
    }

    private DateTime GetCurrentSegmentStart()
    {
        var now = DateTime.UtcNow;

        var segmentTicks = _config.SegmentSize.Ticks; 
        var remainderTicks = now.Ticks % segmentTicks;
        var timeIntoInterval = TimeSpan.FromTicks(remainderTicks);
        return now - timeIntoInterval;
    }

    private BoundaryResult CalculateBoundaryResult(List<EnergySegment> energySegments)
    {
        var minBoundaryCrossingSegment = energySegments.FirstOrDefault(segment => segment.EstimatedBatteryChargeKwh < _config.MinCapacity);
        if (minBoundaryCrossingSegment is not null)
        {
            return new BoundaryResult
            {
                IsOutOfBounds = true,
                IsMax = false,
                IndexOfBoundaryCrossing = energySegments.IndexOf(minBoundaryCrossingSegment)
            };
        }
        var maxBoundaryCrossingSegment = energySegments.FirstOrDefault(segment => _config.MaxCapacity < segment.EstimatedBatteryChargeKwh);
        if (maxBoundaryCrossingSegment is not null)
        {
            return new BoundaryResult
            {
                IsOutOfBounds = true,
                IsMax = true,
                IndexOfBoundaryCrossing = energySegments.IndexOf(maxBoundaryCrossingSegment)
            };
        }
        return new BoundaryResult
        {
            IsOutOfBounds = false,
            IsMax = null,
            IndexOfBoundaryCrossing = null
        };
    }
}
