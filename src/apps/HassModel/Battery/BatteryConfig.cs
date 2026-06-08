using HomeAssistantGenerated;

namespace src.apps.HassModel.Battery;

public class BatteryConfig
{
    public SensorEntity SolarBatteryStateOfChargeEntity { get; set; }
    public SensorEntity GridIn3DaysEntity { get; set; }
    public SensorEntity GridOut3DaysEntity { get; set; }
    public SensorEntity SolarProduction3DaysEntity { get; set; }
    public SensorEntity BatteryChargeDiff3DaysEntity { get; set; }
    public decimal EstimatedUsageMultiplier { get; set; }
    public SelectEntity BatteryModeSelectEntity { get; set; }
    public string BatteryNoneMode { get; set; }
    public string BatteryChargeMode { get; set; }
    public string BatteryDischargeMode { get; set; }
    public decimal BatteryCapacity { get; set; }
    public decimal MinCapacity { get; set; }
    public decimal MaxCapacity { get; set; }
    public int SegmentSizeMins { get; set; }
    public TimeSpan SegmentSize => TimeSpan.FromMinutes(SegmentSizeMins);
    public int MinForecastHours { get; set; }
    public int MaxPriceLockInWaitSecs { get; set; }
    public int MaxPriceLockInRetryDelaySecs { get; set; }
    public decimal ChargeRateKw { get; set; }
    public decimal SegmentChargeAmountKwh => ChargeRateKw * Convert.ToDecimal(SegmentSize.TotalHours);
    public decimal DischargeRateKw { get; set; }
    public decimal SegmentDischargeAmountKwh => DischargeRateKw * Convert.ToDecimal(SegmentSize.TotalHours);
    public InputSelectEntity CurrentActionLog { get; set; }
    public InputDatetimeEntity CurrentActionEndLog { get; set; }
    public InputDatetimeEntity BatteryUntilLog { get; set; }
    public InputSelectEntity NextActionLog { get; set; }
    public InputNumberEntity NextActionPriceLog { get; set; }
    public InputDatetimeEntity NextActionAtLog { get; set; }
}