using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Entities;
using src.apps.HassModel.AC.MitsubishiClient;
using src.apps.HassModel.AC.MitsubishiClient.Models;

namespace src.apps.HassModel.AC;

[NetDaemonApp]
public class AcControl : IAsyncInitializable
{
    private readonly IMitsubishiClient _mitsubishiClient;
    private readonly IAppConfig<AcConfig> _config;
    private readonly ILogger<AcControl> _logger;
    
    public AcControl(IHaContext ha, INetDaemonScheduler scheduler, IAppConfig<AcConfig> config, ILogger<AcControl> logger, ILogger<MitsubishiClient.MitsubishiClient> mitsubishiLogger)
    {
        _mitsubishiClient = new MitsubishiClient.MitsubishiClient(mitsubishiLogger);
        _config = config;
        _logger = logger;
        foreach (var room in config.Value.Rooms)
        {
            room.AcToggleEntity.StateChanges()
                .SubscribeAsync(acToggleEvent =>
                {
                    _logger.LogInformation("AC Toggled to {IsOn} for {Area}",
                        acToggleEvent.Entity.EntityState.IsOn(),
                        acToggleEvent.Entity.Area);
                    return HandleChange();
                });
            room.SetTemperatureEntity.StateChanges()
                .SubscribeAsync(setTemperatureEvent =>
                {
                    _logger.LogInformation("Temperature set to {Temperature} for {Area}",
                        setTemperatureEvent.Entity.EntityState?.State,
                        setTemperatureEvent.Entity.Area);
                    return HandleChange();
                });
            room.TemperatureSensorEntity.StateChanges()
                .SubscribeAsync(temperatureChangedEvent =>
                {
                    _logger.LogInformation("Temperature changed to {Temperature} for {Area}",
                        temperatureChangedEvent.Entity.EntityState?.State,
                        temperatureChangedEvent.Entity.Area);
                    return HandleChange();
                });
        }

        // ReSharper disable once AsyncVoidLambda
        scheduler.RunEvery(TimeSpan.FromSeconds(60), async () =>
        {
            var currentMeasuredTemp = _mitsubishiClient.State.RoomTemp;
            await _mitsubishiClient.UpdateState();
            if (currentMeasuredTemp != _mitsubishiClient.State.RoomTemp)
            {
                await HandleChange();
            }
        });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to login to mitsubishi client");
        await _mitsubishiClient.Login(_config.Value.MitsubishiUsername, _config.Value.MitsubishiPassword,
            cancellationToken);
        _logger.LogInformation("Successfully logged in to mitsubishi client");
        
        foreach (var room in _config.Value.Rooms)
        {
            await _mitsubishiClient.ToggleZone(room.ZoneId, ShouldEnableZone(room), cancellationToken);
        }

        await _mitsubishiClient.SetMode(AcMode.Cool, cancellationToken);
        await SetTemperature(cancellationToken);
    }

    private async Task HandleChange()
    {
        await SetTemperature();
        
        foreach (var room in _config.Value.Rooms)
        {
            await _mitsubishiClient.ToggleZone(room.ZoneId, ShouldEnableZone(room));
        }
        
        await _mitsubishiClient.ToggleAc(_mitsubishiClient.State.IsAnyZoneOn());
    }

    private async Task SetTemperature(CancellationToken cancellationToken = default)
    {
        await _mitsubishiClient.SetTemperature(_mitsubishiClient.State.RoomTemp - _config.Value.Aggressiveness, cancellationToken);
    }

    private bool ShouldEnableZone(AcRoomConfig room, bool checkAll = true)
    {
        if (!room.IsOn || room.SetTemperature is null || room.CurrentTemperate is null) return false;

        var isAllSetTempReached = checkAll && _config.Value.Rooms.All(zone => !ShouldEnableZone(zone, false));
        var onPoint = room.SetTemperature.Value + (isAllSetTempReached ? 0 : _config.Value.OnTolerance);
        var offPoint = room.SetTemperature.Value - _config.Value.OffTolerance;
        if (room.CurrentTemperate >= onPoint) return true;
        if (room.CurrentTemperate <= offPoint) return false;
        return _mitsubishiClient.State.IsZoneOn(room.ZoneId);
    }
}