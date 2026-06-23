using System.Collections.Generic;
using System.Linq;
using HomeAssistantGenerated;
using NetDaemon.HassModel.Entities;

namespace src.apps.HassModel.AC;

public class AcConfig
{
    public string DefaultProfileName { get; set; }
    public AcProfileConfig DefaultProfile => Profiles.First(mode => mode.Name == DefaultProfileName);
    public IEnumerable<AcProfileConfig> Profiles { get; set; }
    public IEnumerable<AcRoomConfig> Rooms { get; set; }
    public InputBooleanEntity AcOnLogEntity { get; set; }
    public InputSelectEntity AcModeLogEntity { get; set; }
    public InputNumberEntity AcAggressivenessLogEntity { get; set; }
    public InputNumberEntity SocModifierLogEntity { get; set; }
    public SensorEntity SolarBatteryStateOfChargeEntity { get; set; }
    public IEnumerable<SocAdjustConfig> SocAdjusts { get; set; }

    /// <summary>
    /// Default radiant "envelope" coefficient used for the felt-temperature estimate: how strongly a
    /// room's felt temperature leans toward the outdoor temperature. Rooms may override it.
    /// </summary>
    public decimal EnvCoefficient { get; set; } = 0.1M;

    /// <summary>Clamp (°C) on the total felt-temperature offset, so a bad outdoor reading can't drive the unit to extremes.</summary>
    public decimal MaxComfortOffset { get; set; } = 3M;
}

public class AcProfileConfig
{
    public string Name { get; set; }
    public decimal ForceTolerance { get; set; } = 3M;
    public decimal OnTolerance { get; set; } = 1M;
    public decimal OffTolerance { get; set; } = 0.5M;
    public decimal WeatherOffset { get; set; } = 3M;
}

public class SocAdjustConfig
{
    public int ProfileModifier { get; set; } = 0;
    public int SocMin { get; set; }
    public int SocMax { get; set; }
    public int Tolerance { get; set; }
}

public class AcRoomConfig
{
    public string Name { get; set; }
    public SensorEntity TemperatureSensorEntity { get; set; }
    public InputNumberEntity SetTemperatureEntity { get; set; }
    public InputBooleanEntity AcToggleEntity { get; set; }
    public InputSelectEntity AcProfileSelectEntity { get; set; }
    public IEnumerable<BinarySensorEntity>? MotionSensorEntities { get; set; }
    public IEnumerable<BinarySensorEntity>? ContactSensorEntities { get; set; }
    public TimeOnly? MotionEnabledFrom { get; set; }
    public TimeOnly? MotionEnabledTo { get; set; }
    public InputBooleanEntity? ZoneOnLogEntity { get; set; }
    public int ZoneId { get; set; }

    /// <summary>
    /// Optional per-room override of <see cref="AcConfig.EnvCoefficient"/>. Exposure differs by room
    /// — use 0 for an internal room (e.g. a hallway), higher for rooms with large or exposed glazing.
    /// </summary>
    public decimal? EnvCoefficient { get; set; }
    public bool IsOn => AcToggleEntity?.EntityState.IsOn() ?? false;
    public decimal? SetTemperature => Convert.ToDecimal(SetTemperatureEntity?.EntityState?.State);

    public decimal? CurrentTemperate =>
        decimal.TryParse(TemperatureSensorEntity?.EntityState?.State, out var currentTemperature)
            ? currentTemperature
            : null;
}