using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using src.apps.HassModel.Battery.Clients.AmberClient;
using src.apps.HassModel.Battery.Clients.AmberClient.Extensions;
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
            GetAverageSegmentUsage() * Convert.ToDecimal(TimeSpan.FromHours(1) / _config.SegmentSize));
        _config.BatteryUntilLog.SetDatetime(datetime: GetBatteryUntil(energySegments).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        
        var boundaryResult = CalculateBoundaryResult(energySegments);
        var loopCount = 0;
        while (boundaryResult.IsOutOfBounds && loopCount < energySegments.Count)
        {
            var previousBoundaryCrossingIndex = GetPreviousBoundaryCrossingIndex(energySegments, boundaryResult);
            loopCount++;
            if (boundaryResult.IsMax == true)
            {
                var maxPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        segment.WeightedSellPricePerKw is not null &&
                        _config.MinCapacity <= (segment.EstimatedBatteryChargeKwh - _config.SegmentDischargeAmountKwh) &&
                        previousBoundaryCrossingIndex <= index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing)
                    .MaxBy(segment => segment.WeightedSellPricePerKw ?? decimal.MinValue);
                if (maxPriceSegment is null)
                    break;
                var maxPriceSegmentIndex = energySegments.IndexOf(maxPriceSegment);
                maxPriceSegment.Action = EnergySegmentAction.Sell;
                for (var i = maxPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh -= _config.SegmentDischargeAmountKwh;
                }
            }
            if (boundaryResult.IsMax == false)
            {
                var lowestPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        segment.WeightedBuyPricePerKw is not null &&
                        (segment.EstimatedBatteryChargeKwh + _config.SegmentChargeAmountKwh) <= _config.MaxCapacity &&
                        previousBoundaryCrossingIndex <= index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing &&
                        !segment.IsDemandWindow)
                    .MinBy(segment => segment.WeightedBuyPricePerKw ?? decimal.MaxValue);
                if (lowestPriceSegment is null)
                    break;
                var lowestPriceSegmentIndex = energySegments.IndexOf(lowestPriceSegment);
                lowestPriceSegment.Action = EnergySegmentAction.Buy;
                for (var i = lowestPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh += _config.SegmentChargeAmountKwh;
                }
            }
            boundaryResult = CalculateBoundaryResult(energySegments);
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
                    EnergySegmentAction.Buy => Convert.ToDouble(Math.Round(nextAction.BuyPricePerKw ?? 0) / 100),
                    EnergySegmentAction.Sell => Convert.ToDouble(Math.Round(nextAction.SellPricePerKw ?? 0) / 100),
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
        var amberPrices = amberPricesTask.Result ?? [];
        while (amberPrices.IsEstimate() &&
               DateTime.UtcNow < startUtc + TimeSpan.FromSeconds(_config.MaxPriceLockInWaitSecs))
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.MaxPriceLockInRetryDelaySecs));
            amberPrices = await _amberClient.GetCurrentPriceAsync() ?? [];
        }
        var curEnergySegment = new EnergySegment
        {
            EstimatedBatteryChargeKwh = currentBatteryChargeKwh,
            Duration = _config.SegmentSize,
            StartUtc = startUtc
        };
        curEnergySegment.ApplySolarForecast(solarForecast);
        curEnergySegment.ApplyPrice(amberPrices, _config.AdvancedPriceWeight);
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
            curEnergySegment.ApplyPrice(amberPrices, _config.AdvancedPriceWeight);
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
        for (int i = 1; i < energySegments.Count; i++)
        {
            var curSegment = energySegments[i - 1];
            var nextSegment = energySegments[i];
            if (curSegment.EstimatedBatteryChargeKwh <= _config.MinCapacity &&
                nextSegment.EstimatedBatteryChargeKwh < curSegment.EstimatedBatteryChargeKwh)
            {
                return new BoundaryResult
                {
                    IsOutOfBounds = true,
                    IsMax = false,
                    IndexOfBoundaryCrossing = i
                };
            }
            if (_config.MaxCapacity <= curSegment.EstimatedBatteryChargeKwh &&
                curSegment.EstimatedBatteryChargeKwh < nextSegment.EstimatedBatteryChargeKwh)
            {
                return new BoundaryResult
                {
                    IsOutOfBounds = true,
                    IsMax = true,
                    IndexOfBoundaryCrossing = i
                };
            }
        }
        return new BoundaryResult
        {
            IsOutOfBounds = false,
            IsMax = null,
            IndexOfBoundaryCrossing = null
        };
    }

    public DateTime GetBatteryUntil(List<EnergySegment> energySegments)
    {
        return energySegments.FirstOrDefault(segment => segment.EstimatedBatteryChargeKwh < _config.MinCapacity)?.StartUtc ?? DateTime.MaxValue;
    }

    private int GetPreviousBoundaryCrossingIndex(List<EnergySegment> energySegments, BoundaryResult boundaryResult)
    {
        if (boundaryResult.IndexOfBoundaryCrossing is null || boundaryResult.IsMax is null) return 0;
        for (var i = boundaryResult.IndexOfBoundaryCrossing.Value; i >= 0; i--)
        {
            var curSegment = energySegments[i];
            if (boundaryResult.IsMax.Value
                    ? (curSegment.EstimatedBatteryChargeKwh - _config.SegmentDischargeAmountKwh) <= _config.MinCapacity
                    : _config.MaxCapacity <= (curSegment.EstimatedBatteryChargeKwh + _config.SegmentChargeAmountKwh))
            {
                return i;
            }
        }
        return 0;
    }
}