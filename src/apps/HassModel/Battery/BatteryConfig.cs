using System.Collections.Generic;
using HomeAssistantGenerated;

namespace src.apps.HassModel.Battery;

public class BatteryConfig
{
    public SensorEntity SolarBatteryStateOfChargeEntity { get; set; }
    public SensorEntity GridIn3DaysEntity { get; set; }
    public SensorEntity GridOut3DaysEntity { get; set; }
    public SensorEntity SolarProduction3DaysEntity { get; set; }

    // Today's cumulative solar production (kWh, resets at midnight). Fed to Forecast.Solar's `actual`
    // parameter so the same-day forecast is recalibrated against real output. See ForecastSolarClient.
    public SensorEntity SolarProductionTodayEntity { get; set; }
    public SensorEntity BatteryChargeDiff3DaysEntity { get; set; }
    public decimal EstimatedUsageMultiplier { get; set; }

    // Usage multiplier applied INSTEAD of EstimatedUsageMultiplier for segments Amber flags as a demand
    // window. Set higher (e.g. 1.5) to inflate the projected drain through a demand window so the plan
    // reserves more charge and avoids an expensive forced grid import if load spikes. Left at 0 (unset)
    // it falls back to EstimatedUsageMultiplier, i.e. demand windows drain the same as any other segment.
    public decimal DemandWindowUsageMultiplier { get; set; }

    // Cumulative (lifetime) counters used to learn per-time-of-day consumption. Consumption over an
    // interval = ΔgridIn − ΔgridOut + Δsolar − (Δcharge − Δdischarge). The battery charge/discharge
    // counters reset daily (handled in UsageMath). See docs/apps/battery-control.md.
    public SensorEntity GridEnergyInTotalEntity { get; set; }
    public SensorEntity GridEnergyOutTotalEntity { get; set; }
    public SensorEntity SolarLifetimeOutputEntity { get; set; }
    public SensorEntity BatteryEnergyChargingEntity { get; set; }
    public SensorEntity BatteryEnergyDischargingEntity { get; set; }

    // Segmented usage estimate (UsageTracker / UsageMath).
    public int UsageBackfillDays { get; set; }        // history pulled on startup to seed the estimate
    public int UsageMaxWindowSegments { get; set; }   // window cap: closes night/no-solar windows and bounds gaps
    public decimal UsageMaxSegmentKwh { get; set; }   // per-segment sanity cap; larger windows are discarded
    // Three nested averaging windows (days back) and their blend weights (renormalised over windows
    // that have data). Defaults: last 1 day 0.4, last 3 days 0.3, last 7 days 0.3.
    public int UsageWindow1Days { get; set; }
    public decimal UsageWindow1Weight { get; set; }
    public int UsageWindow2Days { get; set; }
    public decimal UsageWindow2Weight { get; set; }
    public int UsageWindow3Days { get; set; }
    public decimal UsageWindow3Weight { get; set; }

    public IEnumerable<(int Days, decimal Weight)> UsageEstimateWindows =>
    [
        (UsageWindow1Days, UsageWindow1Weight),
        (UsageWindow2Days, UsageWindow2Weight),
        (UsageWindow3Days, UsageWindow3Weight)
    ];

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
    // Pessimism for BUY-BEFORE-SELL pairs (charge now, export later). Charging is low-regret — if the
    // forecast sell never materialises the bought energy still displaces household load — so these are judged
    // less conservatively than sell-before-buy (discharge now, refill later) pairs, which keep
    // ArbitragePessimismWeight. Set <= ArbitragePessimismWeight; not 0, so a receding forecast spike can't
    // charge the battery full at face value.
    public decimal ArbitrageBuyBeforeSellWeight { get; set; }
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
    public InputSelectEntity CurrentActionReasonLog { get; set; }
    public InputTextEntity CurrentActionWithPriceLog { get; set; }
    public InputDatetimeEntity CurrentActionEndLog { get; set; }
    public InputDatetimeEntity BatteryUntilLog { get; set; }
    public InputSelectEntity NextActionLog { get; set; }
    public InputSelectEntity NextActionReasonLog { get; set; }
    public InputNumberEntity NextActionPriceLog { get; set; }
    public InputDatetimeEntity NextActionAtLog { get; set; }
}