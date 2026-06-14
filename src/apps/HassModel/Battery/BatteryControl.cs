using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using src.apps.HassModel.Battery.Clients.AmberClient;
using src.apps.HassModel.Battery.Clients.AmberClient.Extensions;
using src.apps.HassModel.Battery.Clients.ForecastSolarClient;
using src.apps.HassModel.Battery.Clients.HaHistoryClient;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Extensions;
using src.apps.HassModel.Battery.Models;
using src.apps.HassModel.Battery.Usage;

namespace src.apps.HassModel.Battery;

[NetDaemonApp]
public class BatteryControl
{
    private readonly AmberClient _amberClient;
    private readonly BatteryConfig _config;
    private readonly ForecastSolarClient _forecastSolarClient;
    private readonly ILogger<BatteryControl> _logger;
    private readonly UsageTracker _usageTracker;

    public BatteryControl(IHaContext ha, INetDaemonScheduler scheduler, IAppConfig<BatteryConfig> config,
        ILogger<BatteryControl> logger, ForecastSolarClient forecastSolarClient, AmberClient amberClient,
        HaHistoryClient haHistoryClient, ILogger<UsageTracker> usageLogger)
    {
        _config = config.Value;
        _logger = logger;
        _forecastSolarClient = forecastSolarClient;
        _amberClient = amberClient;
        _usageTracker = new UsageTracker(_config, haHistoryClient, usageLogger);
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
        var hourlyUsage = GetHourlyUsage();
        _logger.LogInformation(
            "Initialised segments with {SegmentCount} {SegmentStart} - {SegmentEnd} First segment is estimate: {IsEstimate} Hourly usage estimate: {HourlyUsageEstimate}",
            energySegments.Count,
            energySegments.First().StartUtc.ToLocalTime().ToString(),
            energySegments.Last().StartUtc.ToLocalTime().ToString(),
            energySegments.First().IsBuyEstimate,
            hourlyUsage);
        _config.BatteryUntilLog.SetDatetime(datetime: BatteryPlanner.GetBatteryUntil(energySegments, _config).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        BatteryPlanner.OptimiseSegments(energySegments, _config, hourlyUsage);
        BatteryPlanner.ApplyArbitrage(energySegments, _config);

        var currentSegment = energySegments.First();
        var currentAction = currentSegment.Action;
        _config.CurrentActionLog.SelectOption(currentAction.ToString());
        _config.CurrentActionReasonLog.SelectOption(currentSegment.ActionReason.ToString());
        _config.CurrentActionWithPriceLog.SetValue(currentAction switch
        {
            EnergySegmentAction.Buy => $"Buy at {Math.Round(currentSegment.BuyPricePerKw ?? 0)}c",
            EnergySegmentAction.Sell => $"Sell at {Math.Round(currentSegment.SellPricePerKw ?? 0)}c",
            _ => "None"
        });
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
                _config.NextActionReasonLog.SelectOption(EnergySegmentActionReason.NotApplicable.ToString());
                _config.NextActionPriceLog.SetValue(0);
            }
            else
            {
                _config.NextActionLog.SelectOption(nextAction.Action.ToString());
                _config.NextActionReasonLog.SelectOption(nextAction.ActionReason.ToString());
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
            _config.NextActionReasonLog.SelectOption(EnergySegmentActionReason.NotApplicable.ToString());
            _config.NextActionPriceLog.SetValue(0);
        }
        return currentAction;
    }

    private async Task<List<EnergySegment>> InitialiseEnergySegmentsAsync()
    {
        await _usageTracker.EnsureBackfilledAsync();
        _usageTracker.Record();
        var fallbackSegmentUsage = GetAverageSegmentUsage();
        var segmentUsageEstimator = _usageTracker.BuildEstimator(fallbackSegmentUsage);
        var currentBatteryChargeKwh = GetCurrentBatteryChargeKwh();
        var startUtc = GetCurrentSegmentStart();
        _logger.LogInformation(
            "Segment usage estimate for {Time}: {Estimate} kWh (flat fallback {Fallback} kWh)",
            startUtc.ToLocalTime().ToShortTimeString(), segmentUsageEstimator(startUtc), fallbackSegmentUsage);
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
        return BatteryPlanner.BuildSegments(startUtc, currentBatteryChargeKwh, segmentUsageEstimator, solarForecast, amberPrices, _config);
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

    private decimal GetHourlyUsage()
    {
        return GetAverageSegmentUsage() * Convert.ToDecimal(TimeSpan.FromHours(1) / _config.SegmentSize);
    }

    private decimal GetCurrentBatteryChargeKwh()
    {
        return Convert.ToDecimal(_config.SolarBatteryStateOfChargeEntity.State) / 100 * _config.BatteryCapacity;
    }

    private DateTime GetCurrentSegmentStart() => UsageMath.SegmentStart(DateTime.UtcNow, _config.SegmentSize);

}