using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using src.apps.HassModel.Battery.Clients.AmberClient;
using src.apps.HassModel.Battery.Clients.ForecastSolarClient;
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
        ShouldChargeFromGridAsync().Wait();
    }

    private async Task<bool> ShouldChargeFromGridAsync()
    {
        var energySegments = await InitialiseEnergySegmentsAsync();
        return false;
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
}
