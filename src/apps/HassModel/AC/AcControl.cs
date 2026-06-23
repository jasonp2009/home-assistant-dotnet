using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using src.apps.HassModel.AC.MitsubishiClient;
using src.apps.HassModel.AC.MitsubishiClient.Models;
using src.apps.HassModel.Battery.Clients.HaHistoryClient;

namespace src.apps.HassModel.AC;

[NetDaemonApp]
public class AcControl : IAsyncInitializable
{
    private readonly IAppConfig<AcConfig> _config;
    private readonly WeatherEntity _forecastHome;
    private readonly ILogger<AcControl> _logger;
    private readonly IMitsubishiClient _mitsubishiClient;
    private readonly HaHistoryClient _historyClient;
    private readonly Dictionary<int, DateTime> _tempLastChangedDict = new();
    private int _curSocModifier = 0;
    private decimal? _outdoorTempEma;
    private DateTime _outdoorTempEmaUpdatedUtc;

    public AcControl(IHaContext ha, INetDaemonScheduler scheduler, IAppConfig<AcConfig> config,
        ILogger<AcControl> logger, IMitsubishiClient mitsubishiClient, HaHistoryClient historyClient)
    {
        _forecastHome = new WeatherEntities(ha).ForecastHome;
        _mitsubishiClient = mitsubishiClient;
        _historyClient = historyClient;
        _config = config;
        _logger = logger;
        foreach (var room in config.Value.Rooms)
        {
            _tempLastChangedDict.Add(room.ZoneId,
                room?.TemperatureSensorEntity?.EntityState?.LastChanged ?? DateTime.Now);
            room.AcToggleEntity.StateChanges()
                .SubscribeAsync(acToggleEvent =>
                {
                    _logger.LogDebug("AC Toggled to {IsOn} for {Area}",
                        acToggleEvent.Entity.IsOn(),
                        acToggleEvent.Entity.Area);
                    return HandleChange();
                }, _logger);
            room.SetTemperatureEntity.StateChanges()
                .SubscribeAsync(setTemperatureEvent =>
                {
                    _logger.LogDebug("Temperature set to {Temperature} for {Area}",
                        setTemperatureEvent.Entity.State,
                        setTemperatureEvent.Entity.Area);
                    return HandleChange();
                }, _logger);
            room.TemperatureSensorEntity.StateChanges()
                .SubscribeAsync(temperatureChangedEvent =>
                {
                    _logger.LogDebug("Temperature changed to {Temperature} for {Area}",
                        temperatureChangedEvent.Entity.State,
                        temperatureChangedEvent.Entity.Area);
                    if (temperatureChangedEvent?.New?.State is not null &&
                        temperatureChangedEvent?.Old?.State is not null &&
                        _mitsubishiClient?.State?.IsZoneOn(room.ZoneId) == true)
                    {
                        var tempDiff = Convert.ToDecimal(temperatureChangedEvent.New.State) -
                                       Convert.ToDecimal(temperatureChangedEvent.Old.State);
                        var isCooling = _mitsubishiClient.State.SetMode == AcMode.Cool;
                        if ((isCooling && tempDiff < 0) || (!isCooling && tempDiff > 0))
                            _tempLastChangedDict[room.ZoneId] = DateTime.Now;
                    }

                    return HandleChange();
                }, _logger);
            room.AcProfileSelectEntity.StateChanges()
                .SubscribeAsync(acModeChangedEvent =>
                {
                    _logger.LogDebug("AC Mode changed to {AcMode} for {Area}",
                        acModeChangedEvent.Entity.State,
                        acModeChangedEvent.Entity.Area);
                    return HandleChange();
                }, _logger);
            room.MotionSensorEntities?.StateChanges()
                .SubscribeAsync(_ => HandleChange(), _logger);
            room.ContactSensorEntities?.StateChanges()
                .SubscribeAsync(_ => HandleChange(), _logger);
        }

        _forecastHome.StateChanges().SubscribeAsync(_ =>
        {
            UpdateOutdoorTempEma();
            return HandleChange();
        }, _logger);
        _config.Value.SolarBatteryStateOfChargeEntity.StateChanges().SubscribeAsync(_ => HandleSocChange(), _logger);

        scheduler.RunEvery(TimeSpan.FromSeconds(60), () =>
        {
            UpdateOutdoorTempEma();
            var currentMeasuredTemp = _mitsubishiClient.State?.RoomTemp;
            _mitsubishiClient.UpdateState().Wait();
            if (currentMeasuredTemp != _mitsubishiClient.State?.RoomTemp) HandleChange().Wait();
        });
    }

    private decimal CurrentWeatherTemperature => Convert.ToDecimal(_forecastHome.Attributes?.Temperature);

    private decimal? CurrentWeatherTemperatureOrNull =>
        _forecastHome.Attributes?.Temperature is { } t ? Convert.ToDecimal(t) : null;

    // Outdoor temperature smoothed by the EMA (models the building's thermal mass); falls back to the
    // instantaneous reading until the EMA is seeded. Used only by the radiant envelope offset — the
    // WeatherOffset economy gate keeps using the instantaneous CurrentWeatherTemperature.
    private decimal SmoothedWeatherTemperature => _outdoorTempEma ?? CurrentWeatherTemperature;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Attempting to login to mitsubishi client");
        await _mitsubishiClient.Login(cancellationToken);
        _logger.LogDebug("Successfully logged in to mitsubishi client");

        _logger.LogInformation(
            "Felt-temperature control: EnvCoefficient {Env}, MaxComfortOffset {Max}°C, humidity coef {HumCoef} @ ref {RefRh}%, outdoor EMA τ {Tau}h (backfill {Backfill}h)",
            _config.Value.EnvCoefficient, _config.Value.MaxComfortOffset, _config.Value.HumidityCoefficient,
            _config.Value.ReferenceHumidity, _config.Value.OutdoorTempTimeConstantHours, _config.Value.OutdoorTempBackfillHours);

        await SeedOutdoorTempEmaAsync();
        await HandleSocChange(cancellationToken);
        await HandleChange(cancellationToken);
    }

    /// <summary>
    /// Seeds the outdoor-temperature EMA from recent weather history so it starts at a sensible
    /// smoothed value rather than the instantaneous reading after a restart. Falls back to the current
    /// reading when no history is available; either way it then advances to "now".
    /// </summary>
    private async Task SeedOutdoorTempEmaAsync()
    {
        var startUtc = DateTime.UtcNow.AddHours(-_config.Value.OutdoorTempBackfillHours);
        var history = await _historyClient.GetAttributeHistoryAsync(_forecastHome.EntityId, "temperature", startUtc);
        var seed = history is not null ? ComfortMath.SeedEma(history, _config.Value.OutdoorTempTimeConstantHours) : null;
        if (seed is { } s)
        {
            _outdoorTempEma = s.Ema;
            _outdoorTempEmaUpdatedUtc = s.AsOfUtc;
        }

        UpdateOutdoorTempEma(); // advance the seed to the current reading/time (or seed it when there was no history)

        _logger.LogInformation(
            "Outdoor temp EMA seeded from {Count} weather history sample(s) over {Hours}h: smoothed {Smoothed:0.0}°C vs instantaneous {Raw:0.0}°C",
            history?.Count ?? 0, _config.Value.OutdoorTempBackfillHours, SmoothedWeatherTemperature, CurrentWeatherTemperature);
    }

    /// <summary>Folds the current outdoor reading into the EMA (or initialises it on the first call).</summary>
    private void UpdateOutdoorTempEma()
    {
        if (CurrentWeatherTemperatureOrNull is not { } reading) return;
        var nowUtc = DateTime.UtcNow;
        _outdoorTempEma = _outdoorTempEma is { } prev
            ? ComfortMath.EmaStep(prev, _outdoorTempEmaUpdatedUtc, reading, nowUtc, _config.Value.OutdoorTempTimeConstantHours)
            : reading;
        _outdoorTempEmaUpdatedUtc = nowUtc;
    }

    private async Task HandleChange(CancellationToken cancellationToken = default)
    {
        await _mitsubishiClient.SetMode(GetDesiredAcMode(), cancellationToken);
        await SetTemperature(cancellationToken);

        foreach (var room in _config.Value.Rooms)
            await _mitsubishiClient.ToggleZone(room.ZoneId, ShouldEnableZone(room, log: true), cancellationToken);

        await _mitsubishiClient.ToggleAc(_mitsubishiClient.State.IsAnyZoneOn(), cancellationToken);
        await _mitsubishiClient.SetFanMode(
            _mitsubishiClient.State.Zones.Count(zone => zone.IsOn) > 2 ? AcFanMode.High : AcFanMode.Low,
            cancellationToken);
        UpdateLogInputs();
    }

    private async Task SetTemperature(CancellationToken cancellationToken = default)
    {
        if (_mitsubishiClient.State.SetMode is not (AcMode.Cool or AcMode.Heat)) return;
        var isCooling = _mitsubishiClient.State.SetMode is AcMode.Cool;

        var validRooms = _config.Value.Rooms
            .Where(room =>
                (room.IsOn && _mitsubishiClient.State.IsZoneOn(room.ZoneId))
                || (room.ZoneOnLogEntity?.EntityState?.LastChanged is not null
                    && DateTime.Now - room.ZoneOnLogEntity.EntityState.LastChanged.Value <
                    TimeSpan.FromMinutes(5)))
            .ToList();
        var aggressiveness = -1M;
        if (validRooms.Count == 0)
            _logger.LogDebug("No valid rooms to calculate temperate, skipping");
        else
            aggressiveness =
                validRooms
                    .Average(room =>
                    {
                        var tempStateChange = _tempLastChangedDict[room.ZoneId];
                        var zoneOnStateChange = room.ZoneOnLogEntity!.EntityState!.LastChanged!.Value;
                        var lastStateChange = tempStateChange > zoneOnStateChange ? tempStateChange : zoneOnStateChange;
                        var lastStateChangeTimeSpan = DateTime.Now - lastStateChange;
                        var roomAggressiveness = Convert.ToDecimal(lastStateChangeTimeSpan.TotalMinutes / 5) - 1M;
                        _logger.LogDebug("Room {Room} has aggressiveness {Aggressiveness}", room.Name,
                            roomAggressiveness);
                        return roomAggressiveness;
                    });

        _logger.LogDebug("Total aggressiveness is: {Aggressiveness}", aggressiveness);
        _config.Value.AcAggressivenessLogEntity.SetValue(Convert.ToDouble(aggressiveness));

        aggressiveness = Math.Floor(aggressiveness);

        await _mitsubishiClient.SetTemperature(
            _mitsubishiClient.State.RoomTemp +
            (isCooling ? -aggressiveness : aggressiveness), cancellationToken);
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

    private bool ShouldEnableZone(AcRoomConfig room, AcMode? mode = null, bool log = false)
    {
        if (!CheckContactAndMotion(room)) return false;
        mode ??= _mitsubishiClient.State.SetMode;
        if (mode is not (AcMode.Cool or AcMode.Heat)) return false;
        var isCooling = mode is AcMode.Cool;
        if (!room.IsOn || room.SetTemperature is null || room.CurrentTemperate is null) return false;

        var profile = GetEffectiveProfile(room.AcProfileSelectEntity?.State);
        if (profile is null) return false;

        var forcePoint = room.SetTemperature.Value + (isCooling ? profile.ForceTolerance : -profile.ForceTolerance);
        var onPoint = room.SetTemperature.Value + (isCooling ? profile.OnTolerance : -profile.OnTolerance);
        var offPoint = room.SetTemperature.Value + (isCooling ? profile.OffTolerance : -profile.OffTolerance);
        var weatherOffPoint = room.SetTemperature.Value + (isCooling ? -profile.WeatherOffset : profile.WeatherOffset);

        // Regulate the estimated *felt* temperature, not raw air temperature: cold surfaces in winter
        // make a room feel colder than the sensor reads, warm surfaces in summer warmer, and humid
        // air feels warmer than dry air at the same temperature.
        var feltTemp = ComfortMath.FeltTemperature(
            room.CurrentTemperate.Value,
            SmoothedWeatherTemperature,
            room.EnvCoefficient ?? _config.Value.EnvCoefficient,
            _config.Value.MaxComfortOffset,
            room.CurrentHumidity,
            _config.Value.ReferenceHumidity,
            _config.Value.HumidityCoefficient);

        var isAcOn = _mitsubishiClient.State.Power;

        if (log && _logger.IsEnabled(LogLevel.Debug))
        {
            var kEnv = room.EnvCoefficient ?? _config.Value.EnvCoefficient;
            var envOffset = ComfortMath.EnvelopeOffset(room.CurrentTemperate.Value, SmoothedWeatherTemperature, kEnv);
            var humOffset = room.CurrentHumidity is { } rh
                ? ComfortMath.HumidityOffset(room.CurrentTemperate.Value, rh, _config.Value.ReferenceHumidity, _config.Value.HumidityCoefficient)
                : 0M;
            _logger.LogDebug(
                "Felt temp {Room} ({Mode}): air {Air:0.0}°C + envelope {Env:+0.0;-0.0} (outdoor {Outdoor:0.0}°C smoothed, raw {Raw:0.0}°C, kEnv {KEnv}) + humidity {Hum:+0.0;-0.0} (RH {Rh:0}%) = felt {Felt:0.0}°C; set {Set:0.0}°C, force/on/off {Force:0.0}/{On:0.0}/{Off:0.0}, weatherGate {Gate:0.0}°C, acOn {AcOn}",
                room.Name, mode, room.CurrentTemperate.Value, envOffset, SmoothedWeatherTemperature, CurrentWeatherTemperature,
                kEnv, humOffset, room.CurrentHumidity, feltTemp, room.SetTemperature, forcePoint, onPoint, offPoint, weatherOffPoint, isAcOn);
        }

        if (isCooling)
        {
            if (CurrentWeatherTemperature <= weatherOffPoint) return false;
            if (feltTemp >= (isAcOn ? onPoint : forcePoint)) return true;
            if (feltTemp <= offPoint) return false;
        }
        else
        {
            if (CurrentWeatherTemperature >= weatherOffPoint) return false;
            if (feltTemp <= (isAcOn ? onPoint : forcePoint)) return true;
            if (feltTemp >= offPoint) return false;
        }

        return _mitsubishiClient.State.IsZoneOn(room.ZoneId) && mode == _mitsubishiClient.State.SetMode;
    }

    private AcProfileConfig? GetEffectiveProfile(string? setProfileName)
    {
        var currentProfile = _config.Value.Profiles.FirstOrDefault(profile => profile.Name == setProfileName) ?? _config.Value.DefaultProfile;
        var profilesWithIndex = _config.Value.Profiles.Index().ToList();
        var currentProfileIndex = profilesWithIndex.FirstOrDefault(profileWithIndex => profileWithIndex.Item.Name == currentProfile.Name).Index;
        var desiredIndex = currentProfileIndex - _curSocModifier;
        if (desiredIndex <= 0)
        {
            return _config.Value.Profiles.FirstOrDefault();
        }

        if (desiredIndex >= _config.Value.Profiles.Count())
        {
            return null;
        }
        return profilesWithIndex.FirstOrDefault(profileWithIndex => profileWithIndex.Index == desiredIndex).Item;
    }

    private async Task HandleSocChange(CancellationToken cancellationToken = default)
    {
        int curSoc = Convert.ToInt32(_config.Value.SolarBatteryStateOfChargeEntity.State);
        var curSocAdjust =
            _config.Value.SocAdjusts.FirstOrDefault(socAdjust => socAdjust.ProfileModifier == _curSocModifier);
        if ((curSocAdjust.SocMin - curSocAdjust.Tolerance) < curSoc &&
            curSoc < (curSocAdjust.SocMax + curSocAdjust.Tolerance))
        {
            return;
        }

        var newSocAdjust = _config.Value.SocAdjusts.FirstOrDefault(socAdjust =>
            socAdjust.SocMin < curSoc && curSoc <= socAdjust.SocMax);
        if (newSocAdjust is null) return;
        _curSocModifier = newSocAdjust.ProfileModifier;
        _config.Value.SocModifierLogEntity.SetValue(Convert.ToDouble(_curSocModifier));
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