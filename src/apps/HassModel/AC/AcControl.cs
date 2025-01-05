using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using src.apps.HassModel.AC.MitsubishiClient;
using src.apps.HassModel.AC.MitsubishiClient.Models;

namespace src.apps.HassModel.AC;

[NetDaemonApp]
public class AcControl : IAsyncInitializable
{
    private readonly IAppConfig<AcConfig> _config;
    private readonly ILogger<AcControl> _logger;
    private readonly IMitsubishiClient _mitsubishiClient;
    private decimal _currentWeatherTemperature;

    public AcControl(IHaContext ha, INetDaemonScheduler scheduler, IAppConfig<AcConfig> config,
        ILogger<AcControl> logger, IMitsubishiClient mitsubishiClient)
    {
        var forecastHome = new WeatherEntities(ha).ForecastHome;
        _mitsubishiClient = mitsubishiClient;
        _config = config;
        _logger = logger;
        foreach (var room in config.Value.Rooms)
        {
            room.AcToggleEntity.StateChanges()
                .SubscribeAsync(acToggleEvent =>
                {
                    _logger.LogInformation("AC Toggled to {IsOn} for {Area}",
                        acToggleEvent.Entity.IsOn(),
                        acToggleEvent.Entity.Area);
                    return HandleChange();
                }, _logger);
            room.SetTemperatureEntity.StateChanges()
                .SubscribeAsync(setTemperatureEvent =>
                {
                    _logger.LogInformation("Temperature set to {Temperature} for {Area}",
                        setTemperatureEvent.Entity.State,
                        setTemperatureEvent.Entity.Area);
                    return HandleChange();
                }, _logger);
            room.TemperatureSensorEntity.StateChanges()
                .SubscribeAsync(temperatureChangedEvent =>
                {
                    _logger.LogInformation("Temperature changed to {Temperature} for {Area}",
                        temperatureChangedEvent.Entity.State,
                        temperatureChangedEvent.Entity.Area);
                    return HandleChange();
                }, _logger);
            room.AcProfileSelectEntity.StateChanges()
                .SubscribeAsync(acModeChangedEvent =>
                {
                    _logger.LogInformation("AC Mode changed to {AcMode} for {Area}",
                        acModeChangedEvent.Entity.State,
                        acModeChangedEvent.Entity.Area);
                    return HandleChange();
                }, _logger);
            room.MotionSensorEntities?.StateChanges()
                .SubscribeAsync(_ => HandleChange(), _logger);
            room.ContactSensorEntities?.StateChanges()
                .SubscribeAsync(_ => HandleChange(), _logger);
        }

        scheduler.RunEvery(TimeSpan.FromSeconds(60), () =>
        {
            var currentMeasuredTemp = _mitsubishiClient.State.RoomTemp;
            _mitsubishiClient.UpdateState().Wait();
            if (currentMeasuredTemp != _mitsubishiClient.State.RoomTemp) HandleChange().Wait();

            var beforeTemperature = _currentWeatherTemperature;
            _currentWeatherTemperature = Convert.ToDecimal(forecastHome.Attributes!.Temperature);
            if (beforeTemperature != _currentWeatherTemperature)
                _logger.LogInformation("Weather temperature changed to {WeatherTemperature}",
                    _currentWeatherTemperature);
        });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to login to mitsubishi client");
        await _mitsubishiClient.Login(cancellationToken);
        _logger.LogInformation("Successfully logged in to mitsubishi client");

        await HandleChange(cancellationToken);
    }

    private async Task HandleChange(CancellationToken cancellationToken = default)
    {
        await _mitsubishiClient.SetMode(GetDesiredAcMode(), cancellationToken);
        await SetTemperature(cancellationToken);

        foreach (var room in _config.Value.Rooms)
            await _mitsubishiClient.ToggleZone(room.ZoneId, ShouldEnableZone(room), cancellationToken);

        await _mitsubishiClient.ToggleAc(_mitsubishiClient.State.IsAnyZoneOn(), cancellationToken);
        UpdateLogInputs();
    }

    private async Task SetTemperature(CancellationToken cancellationToken = default)
    {
        if (_mitsubishiClient.State.SetMode is not (AcMode.Cool or AcMode.Heat)) return;
        var isCooling = _mitsubishiClient.State.SetMode is AcMode.Cool;

        await _mitsubishiClient.SetTemperature(
            _mitsubishiClient.State.RoomTemp +
            (isCooling ? -_config.Value.Aggressiveness : _config.Value.Aggressiveness), cancellationToken);
    }

    private AcMode GetDesiredAcMode()
    {
        var currentMode = _mitsubishiClient.State.SetMode;
        if (currentMode is not (AcMode.Cool or AcMode.Heat))
            return _config.Value.Rooms.Count(room => ShouldEnableZone(room, AcMode.Cool)) >=
                   _config.Value.Rooms.Count(room => ShouldEnableZone(room, AcMode.Heat))
                ? AcMode.Cool
                : AcMode.Heat;

        if (_config.Value.Rooms.Any(room => ShouldEnableZone(room, currentMode))) return currentMode;
        if (currentMode == AcMode.Cool)
        {
            if (_config.Value.Rooms.Any(room => ShouldEnableZone(room, AcMode.Heat))) return AcMode.Heat;
        }
        else
        {
            if (_config.Value.Rooms.Any(room => ShouldEnableZone(room, AcMode.Cool))) return AcMode.Cool;
        }

        return currentMode;
    }

    private bool ShouldEnableZone(AcRoomConfig room, AcMode? mode = null)
    {
        if (!CheckContactAndMotion(room)) return false;
        mode ??= _mitsubishiClient.State.SetMode;
        if (mode is not (AcMode.Cool or AcMode.Heat)) return false;
        var isCooling = mode is AcMode.Cool;
        if (!room.IsOn || room.SetTemperature is null || room.CurrentTemperate is null) return false;

        var profile =
            _config.Value.Profiles.FirstOrDefault(profile => profile.Name == room.AcProfileSelectEntity?.State)
            ?? _config.Value.DefaultProfile;

        var forcePoint = room.SetTemperature.Value + (isCooling ? profile.ForceTolerance : -profile.ForceTolerance);
        var onPoint = room.SetTemperature.Value + (isCooling ? profile.OnTolerance : -profile.OnTolerance);
        var offPoint = room.SetTemperature.Value + (isCooling ? profile.OffTolerance : -profile.OffTolerance);
        var weatherOffPoint = room.SetTemperature.Value + (isCooling ? -profile.WeatherOffset : profile.WeatherOffset);

        var isAcOn = _mitsubishiClient.State.Power;

        if (isCooling)
        {
            if (_currentWeatherTemperature <= weatherOffPoint) return false;
            if (room.CurrentTemperate >= (isAcOn ? onPoint : forcePoint)) return true;
            if (room.CurrentTemperate <= offPoint) return false;
        }
        else
        {
            if (_currentWeatherTemperature >= weatherOffPoint) return false;
            if (room.CurrentTemperate <= (isAcOn ? onPoint : forcePoint)) return true;
            if (room.CurrentTemperate >= offPoint) return false;
        }

        return _mitsubishiClient.State.IsZoneOn(room.ZoneId);
    }

    private bool CheckContactAndMotion(AcRoomConfig room)
    {
        if (room.MotionEnabledFrom is not null && room.MotionEnabledTo is not null &&
            (DateTime.Now.TimeOfDay < room.MotionEnabledFrom.Value.ToTimeSpan() ||
             room.MotionEnabledTo.Value.ToTimeSpan() < DateTime.Now.TimeOfDay)) return true;
        return (room.ContactSensorEntities is null || !room.ContactSensorEntities.Any(contactSensorEntity =>
                   contactSensorEntity.IsOn() &&
                   contactSensorEntity.EntityState?.LastChanged < DateTime.Now.AddMinutes(-5))) &&
               (room.MotionSensorEntities is null || !room.MotionSensorEntities.All(motionSensorEntity =>
                   motionSensorEntity.IsOff() &&
                   motionSensorEntity.EntityState?.LastChanged < DateTime.Now.AddMinutes(-15)));
    }

    private void UpdateLogInputs()
    {
        var state = _mitsubishiClient.State;
        if (state.Power)
            _config.Value.AcOnLogEntity.TurnOn();
        else
            _config.Value.AcOnLogEntity.TurnOff();

        _config.Value.AcModeLogEntity.SelectOption(state.SetMode.ToString());

        foreach (var room in _config.Value.Rooms)
        {
            if (room.ZoneOnLogEntity is null) continue;
            var isZoneOn = state.IsZoneOn(room.ZoneId);
            if (isZoneOn)
                room.ZoneOnLogEntity.TurnOn();
            else
                room.ZoneOnLogEntity.TurnOff();
        }
    }
}