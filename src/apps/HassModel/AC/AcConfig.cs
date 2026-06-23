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

    /// <summary>Reference relative humidity (%) at which the humidity term contributes nothing; only humidity above/below this shifts the felt temperature.</summary>
    public decimal ReferenceHumidity { get; set; } = 50M;

    /// <summary>
    /// Coefficient on the Steadman vapour-pressure (humidity) term of the felt-temperature estimate.
    /// Deliberately below the textbook Steadman value of 0.33 — the full coefficient is calibrated for
    /// outdoor apparent temperature and over-weights humidity at indoor room temperatures.
    /// </summary>
    public decimal HumidityCoefficient { get; set; } = 0.15M;

    /// <summary>
    /// Time constant (hours) of the outdoor-temperature EMA that feeds the radiant envelope offset.
    /// Models the building's thermal mass: larger = smoother and slower (the walls lag the air). 0
    /// disables smoothing (uses the instantaneous outdoor temperature).
    /// </summary>
    public decimal OutdoorTempTimeConstantHours { get; set; } = 15M;

    /// <summary>Hours of weather history replayed on startup to seed the outdoor-temperature EMA.</summary>
    public int OutdoorTempBackfillHours { get; set; } = 48;
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

    /// <summary>
    /// Optional humidity sensor paired with the room's temperature sensor. When present, it enables
    /// the humidity ("mugginess") term of the felt-temperature estimate.
    /// </summary>
    public SensorEntity? HumiditySensorEntity { get; set; }

    public bool IsOn => AcToggleEntity?.EntityState.IsOn() ?? false;
    public decimal? SetTemperature => Convert.ToDecimal(SetTemperatureEntity?.EntityState?.State);

    public decimal? CurrentTemperate =>
        decimal.TryParse(TemperatureSensorEntity?.EntityState?.State, out var currentTemperature)
            ? currentTemperature
            : null;

    public decimal? CurrentHumidity =>
        decimal.TryParse(HumiditySensorEntity?.EntityState?.State, out var currentHumidity)
            ? currentHumidity
            : null;
}