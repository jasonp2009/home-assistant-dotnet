using System.Collections.Generic;
using System.Linq;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;

namespace src.apps.HassModel.LightAdjust;

[NetDaemonApp]
public class LightAdjust
{
    private readonly ILogger<LightAdjust> _logger;
    private readonly Dictionary<string, AdjustmentConfig> _scheduledChanges = new();

    public LightAdjust(IAppConfig<LightAdjustConfig> config, INetDaemonScheduler scheduler, ILogger<LightAdjust> logger)
    {
        _logger = logger;
        foreach (var adjustmentGroup in config.Value.AdjustmentGroups)
        foreach (var light in adjustmentGroup.Lights)
        {
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
            foreach (var adjustment in adjustmentGroup.Adjustments)
            {
                var firstRunDate = DateTime.Now.TimeOfDay >= adjustment.Time.ToTimeSpan()
                    ? DateTime.Today + TimeSpan.FromDays(1)
                    : DateTime.Today;

                var firstRun = new DateTimeOffset(
                    DateOnly.FromDateTime(firstRunDate),
                    adjustment.Time,
                    TimeSpan.FromHours(11));
                scheduler.RunEvery(TimeSpan.FromDays(1), firstRun, () => ApplyAdjustment(light, adjustment));
            }

            var currentAdjustment = adjustmentGroup.Adjustments.OrderBy(adjustment => adjustment.Time)
                                        .LastOrDefault(adjustment =>
                                            adjustment.Time.ToTimeSpan() < DateTime.Now.TimeOfDay)
                                    ?? adjustmentGroup.Adjustments.MaxBy(adjustment => adjustment.Time);
            if (currentAdjustment is not null) ApplyAdjustment(light, currentAdjustment);
        }
    }

    private void ApplyAdjustment(LightEntity light, AdjustmentConfig adjustment)
    {
        if (light.IsOn())
        {
            _logger.LogInformation(
                "Adjusting light {Light} in {Room} to Transition: {Transition} Kelvin: {Kelvin} BrightnessPct: {BrightnessPct}",
                light.Attributes?.FriendlyName ?? light.EntityId, light.Registration?.Area?.Name,
                adjustment.Transition,
                adjustment.Kelvin, adjustment.BrightnessPct);
            light.TurnOn(adjustment.Transition, kelvin: adjustment.Kelvin,
                brightnessPct: adjustment.BrightnessPct);
        }
        else
        {
            _logger.LogInformation(
                "Light {Light} in {Room} is off, will adjust on next state change to Transition: {Transition} Kelvin: {Kelvin} BrightnessPct: {BrightnessPct}",
                light.Attributes?.FriendlyName ?? light.EntityId, light.Registration?.Area?.Name,
                adjustment.Transition,
                adjustment.Kelvin, adjustment.BrightnessPct);
            _scheduledChanges[light.EntityId] = adjustment;
        }
    }
}