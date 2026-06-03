using System.Collections.Generic;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;

namespace src.apps.HassModel.Battery;

[NetDaemonApp]
public class BatteryControl
{
    private BatteryConfig _config;
    private ForecastSolarClient.ForecastSolarClient _forecastSolarClient;
    
    public BatteryControl(IHaContext ha, INetDaemonScheduler scheduler, IAppConfig<BatteryConfig> config,
        ILogger<BatteryControl> logger, ForecastSolarClient.ForecastSolarClient forecastSolarClient)
    {
        _config = config.Value;
        _forecastSolarClient = forecastSolarClient;
        _forecastSolarClient.GetForecastAsync().Wait();
        ShouldChargeFromGrid();
    }

    private bool ShouldChargeFromGrid()
    {
        var energySegments = InitialiseEnergySegments();

        return false;
    }

    private List<EnergySegment> InitialiseEnergySegments()
    {
        var averageHalfHourUsage = GetAverageHalfAnHourUsage();
        var currentBatteryChargeKwh = GetCurrentBatteryChargeKwh();
        currentBatteryChargeKwh = 40;
        var curEnergySegment = new EnergySegment()
        {
            BatteryChargeKwh = currentBatteryChargeKwh
        };
        var energySegments = new List<EnergySegment> {curEnergySegment};
        while (curEnergySegment.BatteryChargeKwh > _config.MinCapacity)
        {
            curEnergySegment = new EnergySegment()
            {
                BatteryChargeKwh = curEnergySegment.BatteryChargeKwh - averageHalfHourUsage
            };
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
}
