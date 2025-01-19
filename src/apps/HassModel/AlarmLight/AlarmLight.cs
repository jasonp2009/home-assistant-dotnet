using System.Collections.Generic;
using System.Linq;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using src.apps.Extensions;

namespace src.apps.HassModel.AlarmLight;

[NetDaemonApp]
public class AlarmLight
{
    private readonly ILogger<AlarmLight> _logger;
    private readonly List<AlarmLightSchedule> _schedules = new();

    public AlarmLight(IAppConfig<AlarmLightConfig> config, INetDaemonScheduler scheduler, ILogger<AlarmLight> logger)
    {
        _logger = logger;
        foreach (var group in config.Value.Groups)
        {
            HandleStateChange(group);
            group.NextAlarm.StateChanges().Subscribe(_ => HandleStateChange(group));
        }

        var nextMinute = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour,
            DateTime.Now.Minute, 0, DateTime.Now.Kind);
        nextMinute = nextMinute.AddMinutes(1);
        scheduler.RunEvery(TimeSpan.FromMinutes(1), nextMinute, ExecuteSchedules);
    }

    private void HandleStateChange(AlarmLightGroup group)
    {
        var schedule = _schedules.FirstOrDefault(schedule =>
            schedule.Group.NextAlarm.EntityId == group.NextAlarm.EntityId);
        if (schedule is not null && !schedule.IsExecuting) _schedules.Remove(schedule);

        if (schedule is not null && schedule.IsExecuting)
        {
            _logger.LogInformation("Schedule executing for {GroupName}. Ignoring next alarm change", group.Name);
            return;
        }

        if (!DateTime.TryParse(group.NextAlarm.State, out var alarmTime)) return;

        _logger.LogInformation("Scheduling alarm light for {GroupName} at {AlarmTime}", group.Name, alarmTime);
        _schedules.Add(new AlarmLightSchedule
        {
            AlarmTime = alarmTime,
            Group = group
        });
    }

    private void ExecuteSchedules()
    {
        var completedSchedules = new List<AlarmLightSchedule>();
        foreach (var schedule in _schedules.Where(schedule => schedule.AlarmTime.AddMinutes(-30) <= DateTime.Now))
        {
            schedule.IsExecuting = true;
            var alarmOffset = DateTime.Now - schedule.AlarmTime;
            var temperaturePct = 100 * (alarmOffset <= TimeSpan.Zero ? 0 : alarmOffset / TimeSpan.FromMinutes(30));
            var brightnessPct = 100 * (alarmOffset >= TimeSpan.Zero ? 1 : 1 + alarmOffset / TimeSpan.FromMinutes(30));
            _logger.LogInformation(
                "Settings lights for {GroupName} to temperature: {TemperaturePct}% and brightness: {BrightnessPct}%",
                schedule.Group.Name, temperaturePct, brightnessPct);
            foreach (var light in schedule.Group.Lights)
                light.TurnOn(kelvin: light.PercentageToKelvin(temperaturePct),
                    brightnessPct: brightnessPct.CleanPercentage());
            if (alarmOffset >= TimeSpan.FromMinutes(30)) completedSchedules.Add(schedule);
        }

        foreach (var completedSchedule in completedSchedules) _schedules.Remove(completedSchedule);
    }
}