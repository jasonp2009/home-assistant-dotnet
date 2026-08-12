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

    // Net movement in the conditioned direction accumulated since the stall clock last reset. The room
    // sensors quantise at 0.1 C, so a single tick is not evidence the room is responding.
    private readonly Dictionary<int, decimal> _tempProgressDict = new();

    // Rate limiting for the Information-level telemetry (see LogRoomSummary).
    private readonly Dictionary<int, (DateTime LoggedAt, bool Enable, ZoneVeto Veto)> _lastRoomLogDict = new();
    private DateTime _driveLoggedAt;
    private decimal _driveLogged;
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
            _tempProgressDict.Add(room!.ZoneId, 0M);
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
                        // Only a sustained move counts as the room responding: accumulate net progress
                        // and reset the stall clock once it clears the threshold. Resetting on a single
                        // 0.1 C tick pinned the drive at -1 through the middle of heating cycles.
                        var (progress, reached) = DriveMath.AccumulateProgress(
                            _tempProgressDict.GetValueOrDefault(room.ZoneId), tempDiff, isCooling,
                            _config.Value.DriveProgressThreshold);
                        _tempProgressDict[room.ZoneId] = progress;
                        if (reached) _tempLastChangedDict[room.ZoneId] = DateTime.Now;
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
        // Entity StateChanges() subscriptions are wired up in the constructor, so a HA state change can
        // arrive before Login() (awaited in InitializeAsync) has populated the Mitsubishi state. Every
        // branch below dereferences _mitsubishiClient.State, so bail out until it's available rather than
        // throwing a NullReferenceException out of the subscription callback.
        if (_mitsubishiClient.State is null)
        {
            _logger.LogDebug("Mitsubishi client state not yet available, skipping change");
            return;
        }

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

    /// <summary>
    /// Sets how hard the unit drives, as an offset from its own return-air temperature. See
    /// <see cref="DriveMath"/> for the shape of the calculation — in particular why a negative drive
    /// (the coil-residual coast) is only allowed near a room's off-point, and why the rooms are
    /// aggregated with Max rather than an average.
    /// </summary>
    private async Task SetTemperature(CancellationToken cancellationToken = default)
    {
        var mode = _mitsubishiClient.State.SetMode;
        if (mode is not (AcMode.Cool or AcMode.Heat)) return;
        var isCooling = mode is AcMode.Cool;

        // Rooms the unit is currently working for: zone open now, or closed within the last 5 minutes
        // (so the drive doesn't collapse the instant a zone satisfies).
        var validRooms = _config.Value.Rooms
            .Where(room =>
                (room.IsOn && _mitsubishiClient.State.IsZoneOn(room.ZoneId))
                || (room.ZoneOnLogEntity?.EntityState?.LastChanged is not null
                    && DateTime.Now - room.ZoneOnLogEntity.EntityState.LastChanged.Value <
                    TimeSpan.FromMinutes(5)))
            .ToList();

        var driveRooms = new List<DriveMath.RoomDrive>();
        foreach (var room in validRooms)
        {
            if (GetComfortPoints(room, mode) is not { } points) continue;

            // Time since the room last made real progress, or since its zone opened — whichever is later.
            var lastProgress = _tempLastChangedDict.GetValueOrDefault(room.ZoneId, DateTime.Now);
            var zoneOpened = room.ZoneOnLogEntity?.EntityState?.LastChanged;
            if (zoneOpened is { } opened && opened > lastProgress) lastProgress = opened;

            var roomDrive = new DriveMath.RoomDrive(points.FeltTemp, points.OffPoint,
                (DateTime.Now - lastProgress).TotalMinutes);
            driveRooms.Add(roomDrive);

            _logger.LogDebug(
                "Drive {Room}: felt {Felt:0.0}°C vs off-point {Off:0.0}°C (error {Error:0.0}), stalled {Stalled:0.0} min -> {Drive:0.00}",
                room.Name, points.FeltTemp, points.OffPoint,
                DriveMath.Error(points.FeltTemp, points.OffPoint, isCooling), roomDrive.MinutesSinceProgress,
                DriveMath.ForRoom(roomDrive, isCooling, _config.Value.DriveCoastWindow, _config.Value.DriveErrorGain));
        }

        if (driveRooms.Count == 0) _logger.LogDebug("No valid rooms to calculate temperate, skipping");

        // Log the unrounded figure — it keeps the fractional resolution that makes the drive legible in
        // history — but command the whole degrees the unit actually accepts.
        var rawDrive = DriveMath.RawForUnit(driveRooms, isCooling, _config.Value.DriveCoastWindow, _config.Value.DriveErrorGain);
        var drive = DriveMath.ForUnit(driveRooms, isCooling, _config.Value.DriveCoastWindow,
            _config.Value.DriveErrorGain, _config.Value.MaxDrive);

        _logger.LogDebug("Total drive is {Raw:0.00} -> commanding {Drive:0.00} °C past return air {RoomTemp:0.0}°C",
            rawDrive, drive, _mitsubishiClient.State.RoomTemp);
        _config.Value.AcAggressivenessLogEntity.SetValue(Convert.ToDouble(rawDrive));

        // Same rate limiting as the per-room summary: paced in the steady state, immediate on a change.
        if (drive != _driveLogged ||
            DateTime.Now - _driveLoggedAt >= TimeSpan.FromMinutes(_config.Value.FeltLogIntervalMinutes))
        {
            (_driveLoggedAt, _driveLogged) = (DateTime.Now, drive);
            _logger.LogInformation(
                "Drive {Now:yyyy-MM-dd HH:mm} ({Mode}): {Rooms} room(s) driving, raw {Raw:0.00} -> {Drive:0.00}°C past return air {RoomTemp:0.0}°C",
                DateTime.Now, mode, driveRooms.Count, rawDrive, drive, _mitsubishiClient.State.RoomTemp);
        }

        await _mitsubishiClient.SetTemperature(
            _mitsubishiClient.State.RoomTemp + (isCooling ? -drive : drive), cancellationToken);
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

    /// <summary>
    /// The felt temperature and hysteresis thresholds for a room in a given mode — everything the zone
    /// decision compares, and everything the drive calculation needs. Null when the room is not
    /// evaluable (missing setpoint or reading, or the SoC shift has pushed it past the last profile).
    /// </summary>
    private readonly record struct ComfortPoints(
        decimal FeltTemp,
        decimal EnvOffset,
        decimal HumidityOffset,
        decimal KEnv,
        decimal ForcePoint,
        decimal OnPoint,
        decimal OffPoint,
        decimal WeatherOffPoint);

    private ComfortPoints? GetComfortPoints(AcRoomConfig room, AcMode mode)
    {
        if (room.SetTemperature is null || room.CurrentTemperate is null) return null;

        var profile = GetEffectiveProfile(room.AcProfileSelectEntity?.State);
        if (profile is null) return null;

        var isCooling = mode is AcMode.Cool;
        var airTemp = room.CurrentTemperate.Value;
        var kEnv = room.EnvCoefficient ?? _config.Value.EnvCoefficient;

        // Regulate the estimated *felt* temperature, not raw air temperature: cold surfaces in winter
        // make a room feel colder than the sensor reads, warm surfaces in summer warmer, and humid
        // air feels warmer than dry air at the same temperature.
        var feltTemp = ComfortMath.FeltTemperature(
            airTemp,
            SmoothedWeatherTemperature,
            kEnv,
            _config.Value.MaxComfortOffset,
            room.CurrentHumidity,
            _config.Value.ReferenceHumidity,
            _config.Value.HumidityCoefficient);

        return new ComfortPoints(
            feltTemp,
            ComfortMath.EnvelopeOffset(airTemp, SmoothedWeatherTemperature, kEnv),
            room.CurrentHumidity is { } rh
                ? ComfortMath.HumidityOffset(airTemp, rh, _config.Value.ReferenceHumidity, _config.Value.HumidityCoefficient)
                : 0M,
            kEnv,
            room.SetTemperature.Value + (isCooling ? profile.ForceTolerance : -profile.ForceTolerance),
            room.SetTemperature.Value + (isCooling ? profile.OnTolerance : -profile.OnTolerance),
            room.SetTemperature.Value + (isCooling ? profile.OffTolerance : -profile.OffTolerance),
            room.SetTemperature.Value + (isCooling ? -profile.WeatherOffset : profile.WeatherOffset));
    }

    /// <summary>Why a zone was refused before the felt-temperature comparison could even run.</summary>
    private enum ZoneVeto
    {
        None,
        Occupancy,       // door left open, or the room has been empty long enough
        NotConditioning, // the unit is not in Cool or Heat
        SwitchedOff,     // the room's own AC toggle is off
        NoReading,       // missing setpoint or temperature sensor value
        NoProfile        // the SoC shift pushed this room past the last profile: zone off entirely
    }

    private (bool Enable, ZoneVeto Veto, ComfortPoints? Points) EvaluateZone(AcRoomConfig room, AcMode mode)
    {
        if (!CheckContactAndMotion(room)) return (false, ZoneVeto.Occupancy, null);
        if (mode is not (AcMode.Cool or AcMode.Heat)) return (false, ZoneVeto.NotConditioning, null);
        if (!room.IsOn) return (false, ZoneVeto.SwitchedOff, null);
        if (room.SetTemperature is null || room.CurrentTemperate is null) return (false, ZoneVeto.NoReading, null);
        if (GetComfortPoints(room, mode) is not { } points) return (false, ZoneVeto.NoProfile, null);

        var isCooling = mode is AcMode.Cool;
        var isAcOn = _mitsubishiClient.State.Power;
        var feltTemp = points.FeltTemp;

        if (isCooling)
        {
            if (CurrentWeatherTemperature <= points.WeatherOffPoint) return (false, ZoneVeto.None, points);
            if (feltTemp >= (isAcOn ? points.OnPoint : points.ForcePoint)) return (true, ZoneVeto.None, points);
            if (feltTemp <= points.OffPoint) return (false, ZoneVeto.None, points);
        }
        else
        {
            if (CurrentWeatherTemperature >= points.WeatherOffPoint) return (false, ZoneVeto.None, points);
            if (feltTemp <= (isAcOn ? points.OnPoint : points.ForcePoint)) return (true, ZoneVeto.None, points);
            if (feltTemp >= points.OffPoint) return (false, ZoneVeto.None, points);
        }

        return (_mitsubishiClient.State.IsZoneOn(room.ZoneId) && mode == _mitsubishiClient.State.SetMode,
            ZoneVeto.None, points);
    }

    private bool ShouldEnableZone(AcRoomConfig room, AcMode? mode = null, bool log = false)
    {
        mode ??= _mitsubishiClient.State.SetMode;
        var (enable, veto, points) = EvaluateZone(room, mode.Value);
        if (log) LogRoomSummary(room, mode.Value, enable, veto, points);
        return enable;
    }

    /// <summary>
    /// Emits the per-room felt-temperature telemetry. The full breakdown stays at <c>Debug</c> for deep
    /// dives, but the deployed add-on journal only holds about seven days at that rate, so the line that
    /// actually has to survive long enough to judge a tuning change is a rate-limited <c>Information</c>
    /// summary: one per room per <see cref="AcConfig.FeltLogIntervalMinutes"/>, plus one immediately
    /// whenever the decision changes. It carries a full date, because the journal's own stamps are
    /// time-only, and it is emitted for vetoed rooms too — those previously produced no line at all,
    /// which silently hid exactly the rooms worth investigating.
    /// </summary>
    private void LogRoomSummary(AcRoomConfig room, AcMode mode, bool enable, ZoneVeto veto, ComfortPoints? points)
    {
        if (_logger.IsEnabled(LogLevel.Debug) && points is { } debugPoints)
            _logger.LogDebug(
                "Felt temp {Room} ({Mode}): air {Air:0.0}°C + envelope {Env:+0.0;-0.0} (outdoor {Outdoor:0.0}°C smoothed, raw {Raw:0.0}°C, kEnv {KEnv}) + humidity {Hum:+0.0;-0.0} (RH {Rh:0}%) = felt {Felt:0.0}°C; set {Set:0.0}°C, force/on/off {Force:0.0}/{On:0.0}/{Off:0.0}, weatherGate {Gate:0.0}°C, acOn {AcOn}",
                room.Name, mode, room.CurrentTemperate!.Value, debugPoints.EnvOffset, SmoothedWeatherTemperature,
                CurrentWeatherTemperature, debugPoints.KEnv, debugPoints.HumidityOffset, room.CurrentHumidity,
                debugPoints.FeltTemp, room.SetTemperature, debugPoints.ForcePoint, debugPoints.OnPoint,
                debugPoints.OffPoint, debugPoints.WeatherOffPoint, _mitsubishiClient.State.Power);

        var previous = _lastRoomLogDict.GetValueOrDefault(room.ZoneId);
        var decisionChanged = previous.LoggedAt == default || previous.Enable != enable || previous.Veto != veto;
        if (!decisionChanged &&
            DateTime.Now - previous.LoggedAt < TimeSpan.FromMinutes(_config.Value.FeltLogIntervalMinutes)) return;
        _lastRoomLogDict[room.ZoneId] = (DateTime.Now, enable, veto);

        if (points is not { } p)
        {
            _logger.LogInformation("Felt {Now:yyyy-MM-dd HH:mm} {Room} ({Mode}): zone off — {Veto}",
                DateTime.Now, room.Name, mode, veto);
            return;
        }

        _logger.LogInformation(
            "Felt {Now:yyyy-MM-dd HH:mm} {Room} ({Mode}): air {Air:0.0} + env {Env:+0.0;-0.0} + hum {Hum:+0.0;-0.0} (RH {Rh:0}%) = felt {Felt:0.0}°C | set {Set:0.0}, force/on/off {Force:0.0}/{On:0.0}/{Off:0.0} | outdoor {Outdoor:0.0} smoothed ({Raw:0.0} raw) | zone {Zone}",
            DateTime.Now, room.Name, mode, room.CurrentTemperate!.Value, p.EnvOffset, p.HumidityOffset,
            room.CurrentHumidity, p.FeltTemp, room.SetTemperature, p.ForcePoint, p.OnPoint, p.OffPoint,
            SmoothedWeatherTemperature, CurrentWeatherTemperature, enable ? "ON" : "off");
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