using System.Linq;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.HassModel.Entities;
using src.apps.Extensions;

namespace src.apps.HassModel.LightSync;

[NetDaemonApp]
public class LightSync
{
    public LightSync(IAppConfig<LightSyncConfig> config, ILogger<LightSync> logger)
    {
        foreach (var zone in config.Value.Zones)
            zone.PrimaryLight.StateAllChanges().Subscribe(primaryLightEvent =>
            {
                if (primaryLightEvent.Entity.IsOff())
                {
                    foreach (var secondaryLight in zone.SecondaryLightGroups.SelectMany(secondaryLightGroup =>
                                 secondaryLightGroup.Lights))
                        secondaryLight.TurnOff();

                    return;
                }

                var primaryLightBrightness = zone.PrimaryLight.Attributes?.Brightness ?? 255;

                foreach (var secondaryLightGroup in zone.SecondaryLightGroups)
                    if (primaryLightBrightness < secondaryLightGroup.OnAt)
                        foreach (var secondaryLight in secondaryLightGroup.Lights)
                            secondaryLight.TurnOff();
                    else
                        foreach (var secondaryLight in secondaryLightGroup.Lights)
                            secondaryLight.TurnOn(kelvin: secondaryLight.PercentageToKelvin(
                                    zone.PrimaryLight.GetTemperaturePercentage()),
                                brightness: zone.PrimaryLight.Attributes?.Brightness);
            });
    }
}