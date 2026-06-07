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
    private readonly AmberClient _amberClient;
    private readonly BatteryConfig _config;
    private readonly ForecastSolarClient _forecastSolarClient;
    private readonly ILogger<BatteryControl> _logger;

    public BatteryControl(IHaContext ha, INetDaemonScheduler scheduler, IAppConfig<BatteryConfig> config,
        ILogger<BatteryControl> logger, ForecastSolarClient forecastSolarClient, AmberClient amberClient)
    {
        _config = config.Value;
        _logger = logger;
        _forecastSolarClient = forecastSolarClient;
        _amberClient = amberClient;
        var nextRun = GetCurrentSegmentStart() + _config.SegmentSize;
        scheduler.RunEvery(_config.SegmentSize, nextRun, () => Task.Run(async () => await CheckAndUpdateBatteryModeAsync()));
    }

    private async Task CheckAndUpdateBatteryModeAsync()
    {
        var currentAction = EnergySegmentAction.None;
        try
        {
            currentAction = await GetCurrentActionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error getting current battery action {Message}", ex.Message);
        }
        try
        {
            _config.BatteryModeSelectEntity.SelectOption(currentAction switch
            {
                EnergySegmentAction.Buy => _config.BatteryChargeMode,
                EnergySegmentAction.Sell => _config.BatteryDischargeMode,
                _ => _config.BatteryNoneMode
            });
            _logger.LogInformation("Succesfully set battery mode to {Action}", currentAction);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error setting battery mode {Action} {Message}", currentAction, ex.Message);
        }
    }

    private async Task<EnergySegmentAction> GetCurrentActionAsync()
    {
        var energySegments = await InitialiseEnergySegmentsAsync();
        _logger.LogInformation(
            "Initialised segments with {SegmentCount} {SegmentStart} - {SegmentEnd} First segment is estimate: {IsEstimate} Hourly usage estimate: {HourlyUsageEstimate}",
            energySegments.Count,
            energySegments.First().StartUtc.ToLocalTime().ToString(),
            energySegments.Last().StartUtc.ToLocalTime().ToString(),
            energySegments.First().IsEstimatedPrice,
            GetAverageSegmentUsage() * Convert.ToDecimal(TimeSpan.FromHours(1)/_config.SegmentSize));
        var boundaryResult = CalculateBoundaryResult(energySegments);
        var fromIndex = 0;
        var loopCount = 0;
        while (boundaryResult.IsOutOfBounds && loopCount < energySegments.Count)
        {
            loopCount++;
            if (boundaryResult.IsMax == true)
            {
                var maxPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        fromIndex < index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing)
                    .MaxBy(segment => segment.SellPricePerKw ?? decimal.MinValue);
                if (maxPriceSegment is null)
                    break;
                var maxPriceSegmentIndex = energySegments.IndexOf(maxPriceSegment);
                maxPriceSegment.Action = EnergySegmentAction.Sell;
                for (var i = maxPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh -= _config.SegmentChargeAmountKwh;
                }
            }
            if (boundaryResult.IsMax == false)
            {
                var lowestPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        fromIndex < index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing &&
                        !segment.IsDemandWindow)
                    .MinBy(segment => segment.BuyPricePerKw ?? decimal.MaxValue);
                if (lowestPriceSegment is null)
                    break;
                var lowestPriceSegmentIndex = energySegments.IndexOf(lowestPriceSegment);
                lowestPriceSegment.Action = EnergySegmentAction.Buy;
                for (var i = lowestPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh += _config.SegmentChargeAmountKwh;
                }
            }
            var newBoundaryResult = CalculateBoundaryResult(energySegments);
            if (newBoundaryResult.IsOutOfBounds && boundaryResult.IsMax != newBoundaryResult.IsMax)
            {
                fromIndex = boundaryResult.IndexOfBoundaryCrossing!.Value;
            }
            boundaryResult = newBoundaryResult;
        }
        var currentAction = energySegments.First().Action;
        _config.CurrentActionLog.SelectOption(currentAction.ToString());
        var currentActionEnd = energySegments.FirstOrDefault(segment => segment.Action != currentAction);
        if (currentActionEnd is not null)
        {
            _config.CurrentActionEndLog.SetDatetime(datetime: currentActionEnd.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            var currentActionEndIndex = energySegments.IndexOf(currentActionEnd);
            var nextAction = energySegments
                .Select((segment, index) => (segment, index))
                .FirstOrDefault(pair => pair.index >= currentActionEndIndex && pair.segment.Action is not EnergySegmentAction.None)
                .segment;
            if (nextAction is null)
            {
                _config.NextActionLog.SelectOption(EnergySegmentAction.None.ToString());
                _config.NextActionPriceLog.SetValue(0);
            }
            else
            {
                _config.NextActionLog.SelectOption(nextAction.Action.ToString());
                _config.NextActionPriceLog.SetValue(nextAction.Action switch
                {
                    EnergySegmentAction.Buy => Convert.ToDouble(Math.Round(nextAction.BuyPricePerKw ?? 0)/100),
                    EnergySegmentAction.Sell => Convert.ToDouble(Math.Round(nextAction.SellPricePerKw ?? 0)/100),
                    _ => 0
                });
                _config.NextActionAtLog.SetDatetime(datetime: nextAction.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            }
        }
        else
        {
            _config.NextActionLog.SelectOption(EnergySegmentAction.None.ToString());
            _config.NextActionPriceLog.SetValue(0);
        }
        return currentAction;
    }

    private async Task<List<EnergySegment>> InitialiseEnergySegmentsAsync()
    {
        var averageHalfHourUsage = GetAverageSegmentUsage();
        var currentBatteryChargeKwh = GetCurrentBatteryChargeKwh();
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
        var energySegments = new List<EnergySegment>
        {
            curEnergySegment
        };
        while (curEnergySegment.BuyPricePerKw is not null ||
               curEnergySegment.SellPricePerKw is not null ||
               curEnergySegment.StartUtc < startUtc + TimeSpan.FromHours(_config.MinForecastHours))
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
        var batteryUsage = batteryChargeDiff3Days / 100 * _config.BatteryCapacity;
        var usage3Days = gridIn3Days - gridOut3Days + solarProduction3Days - batteryUsage;
        var segmentsIn3Days = Convert.ToDecimal(TimeSpan.FromDays(3) / _config.SegmentSize);
        return usage3Days * _config.EstimatedUsageMultiplier / segmentsIn3Days;
    }

    private decimal GetCurrentBatteryChargeKwh()
    {
        return Convert.ToDecimal(_config.SolarBatteryStateOfChargeEntity.State) / 100 * _config.BatteryCapacity;
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