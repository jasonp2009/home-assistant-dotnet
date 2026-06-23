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

                logger.LogDebug(
                    "Adjusting light {Light} in {Room} to Transition: {Transition} Kelvin: {Kelvin} BrightnessPct: {BrightnessPct}",
                    light.Attributes?.FriendlyName ?? light.EntityId, light.Registration?.Area?.Name,
                    scheduledChange.Transition,
                    scheduledChange.Kelvin, scheduledChange.BrightnessPct);
                stateChange.Entity.TurnOn(
                    (long)scheduledChange.Transition,
                    colorTempKelvin: scheduledChange.Kelvin,
                    brightnessPct: (long)scheduledChange.BrightnessPct);
                _scheduledChanges.Remove(stateChange.Entity.EntityId);
            });
            foreach (var adjustment in adjustmentGroup.Adjustments)
            {
                var firstRunDate = DateTime.Now.TimeOfDay >= adjustment.Time.ToTimeSpan()
                    ? DateTime.Today + TimeSpan.FromDays(1)
                    : DateTime.Today;

                // Use the machine's actual local offset (DST-aware) rather than a hardcoded +11. With a
                // hardcoded +11 during AEST (+10), "today at HH:MM" resolves an hour early in absolute
                // time, so an evening adjustment whose time is within the next hour lands in the past and
                // RunEvery fires it immediately at startup — snapping the lights to the evening preset
                // right after a restart instead of holding the current value.
                var firstRunLocal = firstRunDate + adjustment.Time.ToTimeSpan();
                var firstRun = new DateTimeOffset(firstRunLocal, TimeZoneInfo.Local.GetUtcOffset(firstRunLocal));
                _logger.LogDebug("Scheduling {Light} adjustment for {AdjustmentTime}, first run {FirstRun}",
                    light.EntityId, adjustment.Time, firstRun);
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
            _logger.LogDebug(
                "Adjusting light {Light} in {Room} to Transition: {Transition} Kelvin: {Kelvin} BrightnessPct: {BrightnessPct}",
                light.Attributes?.FriendlyName ?? light.EntityId, light.Registration?.Area?.Name,
                adjustment.Transition,
                adjustment.Kelvin, adjustment.BrightnessPct);
            light.TurnOn((long)adjustment.Transition, colorTempKelvin: adjustment.Kelvin,
                brightnessPct: (long)adjustment.BrightnessPct);
        }
        else
        {
            _logger.LogDebug(
                "Light {Light} in {Room} is off, will adjust on next state change to Transition: {Transition} Kelvin: {Kelvin} BrightnessPct: {BrightnessPct}",
                light.Attributes?.FriendlyName ?? light.EntityId, light.Registration?.Area?.Name,
                adjustment.Transition,
                adjustment.Kelvin, adjustment.BrightnessPct);
            _scheduledChanges[light.EntityId] = adjustment;
        }
    }
}
