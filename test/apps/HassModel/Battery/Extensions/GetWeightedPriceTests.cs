using System;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Extensions;
using src.apps.HassModel.Battery.Models;
using Xunit;

namespace Tests.apps.HassModel.Battery.Extensions;

/// <summary>
/// Tests for the runway-weighted price used to rank buy/sell segments. With Usage = 1 kWh/h and
/// MinCapacity = 8, hours-to-empty == (chargeKwh - 8), so the charge level directly selects the
/// risk regime under the default ramp config.
/// </summary>
public class GetWeightedPriceTests
{
    private const decimal Usage = 1m;

    private static BatteryConfig Config() => new()
    {
        MinCapacity = 8m,
        MaxCapacity = 50m,
        PessimismStartHours = 20m,  // pessimism: charge < 28
        PessimismMaxAtHours = 4m,   // maxed:     charge <= 12
        PessimismMaxWeight = 0.7m,
        OptimismStartHours = 26m,   // optimism:  charge > 34
        OptimismMaxAtHours = 32m,   // maxed:     charge >= 40
        OptimismMaxWeight = 0.3m
    };

    private static EnergySegment BuySeg(decimal chargeKwh, decimal? buyPrice, bool isEstimate, AdvancedPrice? advanced) => new()
    {
        Duration = TimeSpan.FromMinutes(5),
        StartUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EstimatedBatteryChargeKwh = chargeKwh,
        BuyPricePerKw = buyPrice,
        IsBuyEstimate = isEstimate,
        AdvancedBuyPrice = advanced
    };

    private static EnergySegment SellSeg(decimal chargeKwh, decimal? sellPrice, bool isEstimate, AdvancedPrice? advanced) => new()
    {
        Duration = TimeSpan.FromMinutes(5),
        StartUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EstimatedBatteryChargeKwh = chargeKwh,
        SellPricePerKw = sellPrice,
        IsSellEstimate = isEstimate,
        AdvancedSellPrice = advanced
    };

    // ---- locked-in prices bypass weighting (invariant) ----

    [Fact]
    public void Buy_LockedIn_ReturnsRawPrice_RegardlessOfRunway()
    {
        // charge 10 => short runway that would normally inflate, but a locked-in price passes through.
        var seg = BuySeg(10m, buyPrice: 13m, isEstimate: false, advanced: new AdvancedPrice { Low = 5m, Predicted = 10m, High = 20m });
        Assert.Equal(13m, seg.GetWeightedPrice(isBuy: true, Config(), Usage));
    }

    [Fact]
    public void Buy_LockedIn_NullPrice_ReturnsMaxValue()
    {
        var seg = BuySeg(20m, buyPrice: null, isEstimate: false, advanced: null);
        Assert.Equal(decimal.MaxValue, seg.GetWeightedPrice(isBuy: true, Config(), Usage));
    }

    [Fact]
    public void Sell_LockedIn_ReturnsRawPrice()
    {
        var seg = SellSeg(40m, sellPrice: -30m, isEstimate: false, advanced: null);
        Assert.Equal(-30m, seg.GetWeightedPrice(isBuy: false, Config(), Usage));
    }

    // ---- estimated buy with advanced price: blend toward High/Low ----

    [Fact]
    public void Buy_Estimate_NeutralRunway_ReturnsPredicted()
    {
        // charge 31 => hours 23 => neutral => weight 0
        var seg = BuySeg(31m, buyPrice: 25m, isEstimate: true, advanced: new AdvancedPrice { Low = 8m, Predicted = 14m, High = 22m });
        Assert.Equal(14m, seg.GetWeightedPrice(isBuy: true, Config(), Usage), 6);
    }

    [Fact]
    public void Buy_Estimate_ShortRunway_LeansTowardHigh()
    {
        // charge 20 => hours 12 => weight +0.35 => 14*(1-0.35) + 22*0.35 = 16.8
        var seg = BuySeg(20m, buyPrice: 25m, isEstimate: true, advanced: new AdvancedPrice { Low = 8m, Predicted = 14m, High = 22m });
        Assert.Equal(16.8m, seg.GetWeightedPrice(isBuy: true, Config(), Usage), 6);
    }

    [Fact]
    public void Buy_Estimate_DeepRunway_LeansTowardLow()
    {
        // charge 48 => hours 40 => weight -0.3 => 14*0.7 + 8*0.3 = 12.2
        var seg = BuySeg(48m, buyPrice: 25m, isEstimate: true, advanced: new AdvancedPrice { Low = 8m, Predicted = 14m, High = 22m });
        Assert.Equal(12.2m, seg.GetWeightedPrice(isBuy: true, Config(), Usage), 6);
    }

    // ---- estimated price without advanced price (beyond ~24h): ignored entirely ----

    [Fact]
    public void Buy_Estimate_NoAdvanced_IsIgnored_RegardlessOfRunway()
    {
        // An estimate with no ML band is a base-perKwh-only forecast we don't trust: a buy is treated as
        // maximally expensive so MinBy never selects it. The runway no longer changes this.
        Assert.Equal(decimal.MaxValue, BuySeg(12m, buyPrice: 25m, isEstimate: true, advanced: null).GetWeightedPrice(isBuy: true, Config(), Usage)); // short runway
        Assert.Equal(decimal.MaxValue, BuySeg(48m, buyPrice: 25m, isEstimate: true, advanced: null).GetWeightedPrice(isBuy: true, Config(), Usage)); // deep runway
    }

    [Fact]
    public void Sell_Estimate_NoAdvanced_IsIgnored_RegardlessOfRunway()
    {
        // Likewise a sell with no ML band is treated as min-earning so MaxBy never selects it — the
        // far-future "spike" with no advanced band is not acted on until it enters the advanced horizon.
        Assert.Equal(decimal.MinValue, SellSeg(48m, sellPrice: -2049m, isEstimate: true, advanced: null).GetWeightedPrice(isBuy: false, Config(), Usage)); // deep runway
        Assert.Equal(decimal.MinValue, SellSeg(12m, sellPrice: -2049m, isEstimate: true, advanced: null).GetWeightedPrice(isBuy: false, Config(), Usage)); // short runway
    }

    // ---- estimated sell with advanced price ----

    [Fact]
    public void Sell_Estimate_DeepRunway_MoreAttractiveThanPredicted()
    {
        // Full battery (charge 48 => hours 40 => weight -0.3). Feed-in advanced Low=5,Predicted=10,High=15.
        // predicted_sell=-10, adjustor=low_sell=-5, |w|=0.3 => -10*0.7 + (-5)*0.3 = -8.5
        var seg = SellSeg(48m, sellPrice: -10m, isEstimate: true, advanced: new AdvancedPrice { Low = 5m, Predicted = 10m, High = 15m });
        var weighted = seg.GetWeightedPrice(isBuy: false, Config(), Usage);
        Assert.Equal(-8.5m, weighted, 6);
        Assert.True(weighted > -10m); // less-negative => MaxBy ranks it as a better sell (eager when full)
    }

    // ---- ordering property the greedy optimiser relies on ----

    [Fact]
    public void Buy_ShortRunway_PenalisesHigherUncertaintyMore()
    {
        var config = Config();
        // Two estimated buy candidates, same predicted and same short runway, different High spread.
        var tight = BuySeg(20m, 25m, isEstimate: true, advanced: new AdvancedPrice { Low = 12m, Predicted = 14m, High = 16m });
        var wide = BuySeg(20m, 25m, isEstimate: true, advanced: new AdvancedPrice { Low = 6m, Predicted = 14m, High = 28m });

        var tightWeighted = tight.GetWeightedPrice(isBuy: true, config, Usage);
        var wideWeighted = wide.GetWeightedPrice(isBuy: true, config, Usage);

        // Pessimism adds w*(High-Predicted); the wider band is inflated more => less attractive under MinBy.
        Assert.True(wideWeighted > tightWeighted);
    }
}
