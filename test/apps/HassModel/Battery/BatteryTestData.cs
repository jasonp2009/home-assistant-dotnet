using System;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Clients.AmberClient.Enums;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Models;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Builders for battery planner scenarios. Keeps the tests focused on the behaviour under test
/// rather than object-graph plumbing.
/// </summary>
internal static class BatteryTestData
{
    public static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Default config matching the deployed values: 53 kWh battery, [8, 50] kWh working band,
    /// 5-minute segments, 12 kW charge/discharge (= 1 kWh moved per segment), and the default
    /// runway ramps (pessimism 20→4h up to 0.7; optimism 26→32h up to 0.3).
    /// </summary>
    public static BatteryConfig Cfg() => new()
    {
        BatteryCapacity = 53m,
        MinCapacity = 8m,
        MaxCapacity = 50m,
        SegmentSizeMins = 5,
        ChargeRateKw = 12m,
        DischargeRateKw = 12m,
        MinForecastHours = 0,
        EstimatedUsageMultiplier = 1m,
        PessimismStartHours = 20m,
        PessimismMaxAtHours = 4m,
        PessimismMaxWeight = 0.7m,
        OptimismStartHours = 26m,
        OptimismMaxAtHours = 32m,
        OptimismMaxWeight = 0.3m,
        ArbitrageEnabled = true,
        ArbitragePessimismWeight = 0.5m,
        // Kept equal to ArbitragePessimismWeight in the baseline so existing pessimism tests are
        // direction-agnostic; the deployed YAML sets a lower value. Tests exercising the asymmetry set it explicitly.
        ArbitrageBuyBeforeSellWeight = 0.5m,
        RoundTripEfficiency = 0.9m,
        ArbitrageMinMarginPerKwh = 0m
    };

    public static AdvancedPrice Adv(decimal low, decimal predicted, decimal high)
        => new() { Low = low, Predicted = predicted, High = high };

    /// <summary>
    /// Builds a single projected segment. Prices default to "locked" (non-estimate) so the weighting
    /// passes them through unchanged unless a test opts into an estimate. `sell` is the SellPricePerKw
    /// value as ApplyPrice stores it: Amber reports feed-in perKwh as NEGATIVE and ApplyPrice negates
    /// it, so a normal feed-in EARNING is a POSITIVE value here (earn 30c -> sell: 30). A negative
    /// value represents paying to export (a negative feed-in price).
    /// </summary>
    public static EnergySegment Seg(
        int index,
        decimal chargeKwh,
        decimal? buy = null, bool buyLocked = true, AdvancedPrice? advBuy = null,
        decimal? sell = null, bool sellLocked = true, AdvancedPrice? advSell = null,
        bool demand = false)
        => new()
        {
            Duration = TimeSpan.FromMinutes(5),
            StartUtc = Base.AddMinutes(5 * index),
            EstimatedBatteryChargeKwh = chargeKwh,
            BuyPricePerKw = buy,
            IsBuyEstimate = !buyLocked,
            AdvancedBuyPrice = advBuy,
            SellPricePerKw = sell,
            IsSellEstimate = !sellLocked,
            AdvancedSellPrice = advSell,
            IsDemandWindow = demand
        };

    /// <summary>An Amber "general" (buy) interval covering the 5-minute slot at <paramref name="index"/>.</summary>
    public static CurrentInterval GeneralInterval(int index, decimal perKwh, bool estimate = false, AdvancedPrice? advanced = null, bool demand = false)
        => new()
        {
            ChannelType = ChannelType.General,
            PerKwh = perKwh,
            Estimate = estimate,
            AdvancedPrice = advanced,
            StartTime = Base.AddMinutes(5 * index),
            EndTime = Base.AddMinutes(5 * index + 5),
            TariffInformation = new TariffInformation { DemandWindow = demand }
        };

    /// <summary>An Amber "feedIn" (sell) interval covering the 5-minute slot at <paramref name="index"/>.</summary>
    public static CurrentInterval FeedInInterval(int index, decimal perKwh, bool estimate = false, AdvancedPrice? advanced = null)
        => new()
        {
            ChannelType = ChannelType.FeedIn,
            PerKwh = perKwh,
            Estimate = estimate,
            AdvancedPrice = advanced,
            StartTime = Base.AddMinutes(5 * index),
            EndTime = Base.AddMinutes(5 * index + 5)
        };
}
