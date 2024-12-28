using System.Collections.Generic;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;

namespace src.apps.HassModel.LightAdjust;

[NetDaemonApp]
public class LightAdjust
{
    private readonly Dictionary<string, AdjustmentConfig> _scheduledChanges = new();

    public LightAdjust(IAppConfig<LightAdjustConfig> config, INetDaemonScheduler scheduler, ILogger<LightAdjust> logger)
    {
        foreach (var lightConfig in config.Value.Lights)
        {
            var light = lightConfig.LightEntity;
            light.StateChanges().Where(stateChange => stateChange.New.IsOn()).Subscribe(stateChange =>
            {
                if (!_scheduledChanges.TryGetValue(stateChange.Entity.EntityId, out var scheduledChange)) return;

                logger.LogInformation(
                    "Adjusting light {Light} in {Room} to Transition: {Transition} Kelvin: {Kelvin} BrightnessPct: {BrightnessPct}",
                    light.Attributes?.FriendlyName ?? light.EntityId, light.Registration?.Area?.Name,
                    scheduledChange.Transition,
                    scheduledChange.Kelvin, scheduledChange.BrightnessPct);
                stateChange.Entity.TurnOn(
                    scheduledChange.Transition,
                    kelvin: scheduledChange.Kelvin,
                    brightnessPct: scheduledChange.BrightnessPct);
                _scheduledChanges.Remove(stateChange.Entity.EntityId);
            });
            foreach (var adjustment in lightConfig.Adjustments)
            {
                var firstRunDate = DateTime.Now.TimeOfDay >= adjustment.Time.ToTimeSpan()
                    ? DateTime.Today + TimeSpan.FromDays(1)
                    : DateTime.Today;

                var firstRun = new DateTimeOffset(
                    DateOnly.FromDateTime(firstRunDate),
                    adjustment.Time,
                    TimeSpan.FromHours(11));
                scheduler.RunEvery(TimeSpan.FromDays(1), firstRun,
                    () =>
                    {
                        if (light.IsOn())
                        {
                            logger.LogInformation(
                                "Adjusting light {Light} in {Room} to Transition: {Transition} Kelvin: {Kelvin} BrightnessPct: {BrightnessPct}",
                                light.Attributes?.FriendlyName ?? light.EntityId, light.Registration?.Area?.Name,
                                adjustment.Transition,
                                adjustment.Kelvin, adjustment.BrightnessPct);
                            light.TurnOn(adjustment.Transition, kelvin: adjustment.Kelvin,
                                brightnessPct: adjustment.BrightnessPct);
                        }
                        else
                        {
                            logger.LogInformation(
                                "Light {Light} in {Room} is off, will adjust on next state change to Transition: {Transition} Kelvin: {Kelvin} BrightnessPct: {BrightnessPct}",
                                light.Attributes?.FriendlyName ?? light.EntityId, light.Registration?.Area?.Name,
                                adjustment.Transition,
                                adjustment.Kelvin, adjustment.BrightnessPct);
                            _scheduledChanges[light.EntityId] = adjustment;
                        }
                    }
                );
            }
        }
    }
}