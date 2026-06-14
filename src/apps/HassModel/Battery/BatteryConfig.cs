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

    // Risk weighting based on battery runway (hours-to-empty). Pessimism leans an estimated
    // price toward Amber's High bound (buy)/Low bound (sell); optimism leans the other way.
    // Thresholds are absolute hours, so higher usage shifts pessimism in at a higher state of charge.
    public decimal PessimismStartHours { get; set; }  // runway below which pessimism begins ramping
    public decimal PessimismMaxAtHours { get; set; }  // runway at/below which pessimism is maxed
    public decimal PessimismMaxWeight { get; set; }   // max pessimism blend fraction
    public decimal OptimismStartHours { get; set; }   // runway above which optimism begins ramping
    public decimal OptimismMaxAtHours { get; set; }   // runway at/above which optimism is maxed
    public decimal OptimismMaxWeight { get; set; }    // max optimism blend fraction

    // Price arbitrage (buy import low / export high). Uses a fixed pessimistic weight (not the runway weight)
    // so it only commits when confident. Profit gate:
    //   pessimisticSell >= pessimisticBuy / RoundTripEfficiency + ArbitrageMinMarginPerKwh.
    public bool ArbitrageEnabled { get; set; }
    public decimal ArbitragePessimismWeight { get; set; }  // 0..1: how far arbitrage prices lean to High (buy) / low-earning (sell)
    public decimal RoundTripEfficiency { get; set; }       // charge->discharge efficiency, used only in the profit gate
    public decimal ArbitrageMinMarginPerKwh { get; set; }  // minimum profit (price units) required to commit a pair

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