using HomeAssistantGenerated;

namespace src.apps.HassModel.Battery;

public class BatteryConfig
{
    public SensorEntity SolarBatteryStateOfChargeEntity { get; set; }
    public SensorEntity GridIn3DaysEntity { get; set; }
    public SensorEntity GridOut3DaysEntity { get; set; }
    public SensorEntity SolarProduction3DaysEntity { get; set; }
    public SensorEntity BatteryChargeDiff3DaysEntity { get; set; }
    public decimal BatteryCapacity { get; set; }
    public decimal MinCapacity { get; set; }
    public decimal MaxCapacity { get; set; }
    public int SegmentSizeMins { get; set; }
    public TimeSpan SegmentSize => TimeSpan.FromMinutes(SegmentSizeMins);
    public decimal ChargeRateKw { get; set; }
    public decimal SegmentChargeAmountKwh => ChargeRateKw * Convert.ToDecimal(SegmentSize.TotalHours);
    public decimal DischargeRateKw { get; set; }
    public decimal SegmentDischargeAmountKwh => DischargeRateKw * Convert.ToDecimal(SegmentSize.TotalHours);
}