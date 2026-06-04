using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;

namespace src.apps.HassModel.Battery;

[NetDaemonApp]
public class BatteryControl
{
    private BatteryConfig _config;
    private ForecastSolarClient.ForecastSolarClient _forecastSolarClient;
    private AmberClient.AmberClient _amberClient;
    
    public BatteryControl(IHaContext ha, INetDaemonScheduler scheduler, IAppConfig<BatteryConfig> config,
        ILogger<BatteryControl> logger, ForecastSolarClient.ForecastSolarClient forecastSolarClient, AmberClient.AmberClient amberClient)
    {
        _config = config.Value;
        _forecastSolarClient = forecastSolarClient;
        _amberClient = amberClient;
        ShouldChargeFromGridAsync().Wait();
    }

    private async Task<bool> ShouldChargeFromGridAsync()
    {
        var energySegments = await InitialiseEnergySegmentsAsync();
        return false;
    }

    private async Task<List<EnergySegment>> InitialiseEnergySegmentsAsync()
    {
        var averageHalfHourUsage = GetAverageHalfAnHourUsage();
        var currentBatteryChargeKwh = GetCurrentBatteryChargeKwh();
        var startUtc = GetCurrentSegmentStart();
        var solarForecast = await _forecastSolarClient.GetForecastAsync();
        var curEnergySegment = new EnergySegment
        {
            EstimatedBatteryChargeKwh = currentBatteryChargeKwh,
            Duration = _config.SegmentSize,
            StartUtc = startUtc
        };
        curEnergySegment.EstimatedBatteryChargeKwh +=
            GetSolarForecastForPeriod(solarForecast, curEnergySegment.StartUtc, curEnergySegment.EndUtc);
        var energySegments = new List<EnergySegment> {curEnergySegment};
        while (curEnergySegment.EstimatedBatteryChargeKwh > _config.MinCapacity)
        {
            curEnergySegment = new EnergySegment
            {
                EstimatedBatteryChargeKwh = curEnergySegment.EstimatedBatteryChargeKwh - averageHalfHourUsage,
                Duration = _config.SegmentSize,
                StartUtc = curEnergySegment.StartUtc + _config.SegmentSize
            };
            curEnergySegment.EstimatedBatteryChargeKwh +=
                GetSolarForecastForPeriod(solarForecast, curEnergySegment.StartUtc, curEnergySegment.EndUtc);
            energySegments.Add(curEnergySegment);
        }
        return energySegments;
    }

    private decimal GetAverageHalfAnHourUsage()
    {
        var gridIn3Days = Convert.ToDecimal(_config.GridIn3DaysEntity.State);
        var gridOut3Days = Convert.ToDecimal(_config.GridOut3DaysEntity.State);
        var solarProduction3Days = Convert.ToDecimal(_config.SolarProduction3DaysEntity.State);
        var batteryChargeDiff3Days = Convert.ToDecimal(_config.BatteryChargeDiff3DaysEntity.State);
        var batteryUsage = ((batteryChargeDiff3Days / 100) * _config.BatteryCapacity);
        var usage3Days = gridIn3Days - gridOut3Days + solarProduction3Days - batteryUsage;
        return usage3Days / (3 * 24 * 2);
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
    
    private decimal GetSolarForecastForPeriod(Dictionary<DateTime, int>? solarForecast, DateTime startUtc, DateTime endUtc)
    {
        if (solarForecast is null) return 0;
        var solarForecastWatts = Convert.ToDecimal(solarForecast
            .Where(pair => startUtc <= pair.Key && pair.Key < endUtc)
            .Sum(pair => pair.Value));
        return solarForecastWatts / 1000;
    }
}
