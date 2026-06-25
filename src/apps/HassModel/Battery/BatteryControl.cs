using System.Collections.Generic;
using System.Globalization;
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
            _logger.LogError("Error getting current battery action {Message} {StackTrace}", ex.Message, ex.StackTrace);
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
        var (energySegments, currentSegmentUsage, currentChargeKwh) = await InitialiseEnergySegmentsAsync();
        var hourlyUsage = GetHourlyUsage();
        // Log the current segment's time-of-day estimate scaled to an hour (the runway/OptimiseSegments
        // still uses the flat hourly average below — see GetHourlyUsage).
        var hourlyUsageEstimate = currentSegmentUsage * Convert.ToDecimal(TimeSpan.FromHours(1) / _config.SegmentSize);
        _logger.LogInformation(
            "Initialised segments with {SegmentCount} {SegmentStart} - {SegmentEnd} First segment is estimate: {IsEstimate} Hourly usage estimate: {HourlyUsageEstimate}",
            energySegments.Count,
            energySegments.First().StartUtc.ToLocalTime().ToString(),
            energySegments.Last().StartUtc.ToLocalTime().ToString(),
            energySegments.First().IsBuyEstimate,
            hourlyUsageEstimate);
        _config.BatteryUntilLog.SetDatetime(datetime: BatteryPlanner.GetBatteryUntil(energySegments, _config).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        LogPlanningContext(energySegments, currentChargeKwh);

        BatteryPlanner.OptimiseSegments(energySegments, _config, hourlyUsage);
        BatteryPlanner.ApplyArbitrage(energySegments, _config);

        var currentSegment = energySegments.First();
        var currentAction = currentSegment.Action;
        LogDecision(energySegments, currentSegment, currentAction, currentChargeKwh);
        _config.CurrentActionLog.SelectOption(currentAction.ToString());
        // Only surface a meaningful reason (Usage/Arbitrage). When the current segment has no action the
        // reason is NotApplicable; leave the previous value in place rather than overwriting it.
        if (currentSegment.ActionReason is EnergySegmentActionReason.Usage or EnergySegmentActionReason.Arbitrage)
        {
            _config.CurrentActionReasonLog.SelectOption(currentSegment.ActionReason.ToString());
        }
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

    private async Task<(List<EnergySegment> Segments, decimal CurrentSegmentUsageKwh, decimal CurrentChargeKwh)> InitialiseEnergySegmentsAsync()
    {
        await _usageTracker.EnsureBackfilledAsync();
        _usageTracker.Record();
        var fallbackSegmentUsage = GetAverageSegmentUsage();
        var segmentUsageEstimator = _usageTracker.BuildEstimator(fallbackSegmentUsage);
        var currentBatteryChargeKwh = GetCurrentBatteryChargeKwh();
        var startUtc = GetCurrentSegmentStart();
        var currentSegmentUsage = segmentUsageEstimator(startUtc);
        _logger.LogInformation(
            "Segment usage estimate for {Time}: {Estimate} kWh (flat fallback {Fallback} kWh)",
            startUtc.ToLocalTime().ToShortTimeString(), currentSegmentUsage, fallbackSegmentUsage);
        var profileDate = startUtc.ToLocalTime().Date;
        var profile = string.Join("  ", new[] { 0, 3, 6, 9, 12, 15, 18, 21 }
            .Select(h => $"{h:00}h={segmentUsageEstimator(profileDate.AddHours(h).ToUniversalTime()):0.###}"));
        _logger.LogInformation("Per-segment usage estimate profile (kWh per 5min, local time-of-day): {Profile}", profile);
        var actualSolarKwh = GetTodaySolarProductionKwh();
        var solarForecastTask = _forecastSolarClient.GetForecastAsync(actualSolarKwh);
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
        var segments = BatteryPlanner.BuildSegments(startUtc, currentBatteryChargeKwh, segmentUsageEstimator, solarForecast, amberPrices, _config);
        return (segments, currentSegmentUsage, currentBatteryChargeKwh);
    }

    /// <summary>
    /// Logs the measured state of charge and the first capacity limit the projection crosses BEFORE the
    /// solver acts — i.e. what OptimiseSegments is reacting to this cycle (a min crossing => buy needed,
    /// a max crossing => sell needed, or within bounds). Read-only; mirrors <see cref="BatteryPlanner.CalculateBoundaryResult"/>.
    /// </summary>
    private void LogPlanningContext(List<EnergySegment> segments, decimal currentChargeKwh)
    {
        var boundary = BatteryPlanner.CalculateBoundaryResult(segments, _config);
        string description;
        if (!boundary.IsOutOfBounds || boundary.IndexOfBoundaryCrossing is null)
        {
            description = $"trajectory within [{_config.MinCapacity}, {_config.MaxCapacity}] kWh over horizon";
        }
        else
        {
            var crossing = segments[boundary.IndexOfBoundaryCrossing.Value];
            var at = crossing.StartUtc.ToLocalTime().ToShortTimeString();
            description = boundary.IsMax == true
                ? $"MAX crossing at {at} (proj {crossing.EstimatedBatteryChargeKwh:0.0} kWh) -> sell needed"
                : $"MIN crossing at {at} (proj {crossing.EstimatedBatteryChargeKwh:0.0} kWh) -> buy needed";
        }
        _logger.LogInformation("Planning: SoC {Soc:0.0} kWh; {Boundary}", currentChargeKwh, description);
    }

    /// <summary>
    /// Logs the action chosen for "now" with its reason, the resulting projected charge, and the action
    /// band [MinCapacity + step, MaxCapacity - step]. The one-step buffer means a Buy must land at or
    /// below the band top and a Sell at or above the band bottom — a projected charge sitting on a limit
    /// (50 or 8) would mean the buffer regressed. Also lists the boundary-solver (Usage) legs and any
    /// arbitrage legs so the full plan — and whether churn is boundary- or price-arbitrage-driven — is
    /// readable from the log alone.
    /// </summary>
    private void LogDecision(List<EnergySegment> segments, EnergySegment now, EnergySegmentAction action, decimal currentChargeKwh)
    {
        _logger.LogInformation(
            "Now {Action}/{Reason}: SoC {Soc:0.0} -> proj {Proj:0.0} kWh (action band {BandLow:0.0}-{BandHigh:0.0}); buy {Buy}c sell {Sell}c; solar(now) {Solar:0.00} kWh",
            action, now.ActionReason, currentChargeKwh, now.EstimatedBatteryChargeKwh,
            _config.MinCapacity + _config.SegmentDischargeAmountKwh, _config.MaxCapacity - _config.SegmentChargeAmountKwh,
            Math.Round(now.BuyPricePerKw ?? 0), Math.Round(now.SellPricePerKw ?? 0), now.SolarForecastKwh);

        // Usage legs are capped to the next 24 h: near the floor the boundary solver places a buy most
        // nights across the 72 h horizon, so an uncapped line is dominated by far-out keep-alive legs that
        // re-plan well before they fire. Arbitrage legs are sparse (priced spikes), so they stay uncapped.
        LogLegs(segments, EnergySegmentActionReason.Usage, "Usage", within: TimeSpan.FromHours(24));
        LogLegs(segments, EnergySegmentActionReason.Arbitrage, "Arbitrage");
    }

    /// <summary>
    /// Lists the legs the planner committed for a given reason (Usage = boundary solver, Arbitrage =
    /// price arbitrage) as "{Action} {local time}@{price}c", in chronological order, on one line. Buys
    /// show the buy price, sells the sell price. No-op when there are no legs of that reason. When
    /// <paramref name="within"/> is set, only legs starting within that span of the plan's first segment
    /// are listed; any further-out legs are summarised as a trailing "(+N more beyond Hh)" count.
    /// </summary>
    private void LogLegs(List<EnergySegment> segments, EnergySegmentActionReason reason, string label, TimeSpan? within = null)
    {
        var matching = segments
            .Where(s => s.Action != EnergySegmentAction.None && s.ActionReason == reason)
            .OrderBy(s => s.StartUtc)
            .ToList();
        if (matching.Count == 0) return;

        var cutoff = within is null ? DateTime.MaxValue : segments.First().StartUtc + within.Value;
        var shown = matching.Where(s => s.StartUtc < cutoff).ToList();
        var omitted = matching.Count - shown.Count;

        var legs = string.Join(", ", shown.Select(s =>
            $"{s.Action} {s.StartUtc.ToLocalTime().ToShortTimeString()}@{Math.Round((s.Action == EnergySegmentAction.Buy ? s.BuyPricePerKw : s.SellPricePerKw) ?? 0)}c"));
        var more = omitted > 0 ? $" (+{omitted} more beyond {within!.Value.TotalHours:0}h)" : "";
        _logger.LogInformation("{Label} legs this plan: {Legs}{More}", label, legs, more);
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

    /// <summary>
    /// Today's measured cumulative solar production (kWh), passed to Forecast.Solar's <c>actual</c>
    /// parameter to recalibrate the same-day forecast. Returns null when the sensor is missing or
    /// non-numeric (e.g. "unknown"/"unavailable") so the request omits the calibration rather than failing.
    /// </summary>
    private decimal? GetTodaySolarProductionKwh()
    {
        if (decimal.TryParse(_config.SolarProductionTodayEntity.State, NumberStyles.Any, CultureInfo.InvariantCulture, out var kwh)
            && kwh >= 0)
            return kwh;
        _logger.LogWarning("Today's solar production sensor '{Entity}' state '{State}' is not usable; skipping forecast calibration",
            _config.SolarProductionTodayEntity.EntityId, _config.SolarProductionTodayEntity.State);
        return null;
    }

    private DateTime GetCurrentSegmentStart() => UsageMath.SegmentStart(DateTime.UtcNow, _config.SegmentSize);

}