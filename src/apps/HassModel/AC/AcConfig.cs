using System.Collections.Generic;
using HomeAssistantGenerated;
using NetDaemon.HassModel.Entities;

namespace src.apps.HassModel.AC;

public class AcConfig
{
    public string MitsubishiUsername { get; set; }
    public string MitsubishiPassword { get; set; }
    public decimal OnTolerance { get; set; } = 1M;
    public decimal OffTolerance { get; set; } = 0.5M;
    public decimal Aggressiveness { get; set; } = 0;
    public IEnumerable<AcRoomConfig> Rooms { get; set; }
}

public class AcRoomConfig
{
    public string Name { get; set; }
    public SensorEntity TemperatureSensorEntity { get; set; }
    public InputNumberEntity SetTemperatureEntity { get; set; }
    public InputBooleanEntity AcToggleEntity { get; set; }
    public int ZoneId { get; set; }
    public bool IsOn => AcToggleEntity?.EntityState.IsOn() ?? false;
    public decimal? SetTemperature => Convert.ToDecimal(SetTemperatureEntity?.EntityState?.State);

    public decimal? CurrentTemperate =>
        decimal.TryParse(TemperatureSensorEntity?.EntityState?.State, out var currentTemperature)
            ? currentTemperature
            : null;
}