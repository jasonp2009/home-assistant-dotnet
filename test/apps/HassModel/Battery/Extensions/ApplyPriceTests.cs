using System;
using System.Collections.Generic;
using src.apps.HassModel.Battery.Clients.AmberClient.Enums;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Extensions;
using src.apps.HassModel.Battery.Models;
using Xunit;

namespace Tests.apps.HassModel.Battery.Extensions;

/// <summary>
/// Characterisation tests for <see cref="EnergySegmentExtensions.ApplyPrice"/>: how Amber
/// intervals are mapped onto a segment's buy/sell prices. Behaviour here is unchanged by the
/// weighted-price work; these guard against regressions.
/// </summary>
public class ApplyPriceTests
{
    private static readonly DateTime SegStart = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static EnergySegment NewSegment() => new()
    {
        Duration = TimeSpan.FromMinutes(5),
        StartUtc = SegStart,
        EstimatedBatteryChargeKwh = 20m
    };

    private static CurrentInterval Current(ChannelType channel, decimal perKwh, bool estimate,
        AdvancedPrice? advanced = null, bool demandWindow = false, int durationMins = 30) => new()
    {
        ChannelType = channel,
        PerKwh = perKwh,
        Estimate = estimate,
        AdvancedPrice = advanced,
        StartTime = SegStart,
        EndTime = SegStart.AddMinutes(durationMins),
        TariffInformation = new TariffInformation { DemandWindow = demandWindow }
    };

    private static ForecastInterval Forecast(ChannelType channel, decimal perKwh,
        AdvancedPrice? advanced = null, DateTime? start = null, int durationMins = 30) => new()
    {
        ChannelType = channel,
        PerKwh = perKwh,
        AdvancedPrice = advanced,
        StartTime = start ?? SegStart,
        EndTime = (start ?? SegStart).AddMinutes(durationMins)
    };

    [Fact]
    public void ApplyPrice_NullIntervals_LeavesSegmentUnchanged()
    {
        var seg = NewSegment();

        seg.ApplyPrice(null);

        Assert.Null(seg.BuyPricePerKw);
        Assert.Null(seg.SellPricePerKw);
        Assert.True(seg.IsBuyEstimate);  // defaults preserved
        Assert.True(seg.IsSellEstimate);
    }

    [Fact]
    public void ApplyPrice_GeneralChannel_SetsBuyPriceAndLockedInFlag()
    {
        var seg = NewSegment();

        seg.ApplyPrice(new List<BaseInterval> { Current(ChannelType.General, perKwh: 25m, estimate: false) });

        Assert.Equal(25m, seg.BuyPricePerKw);
        Assert.False(seg.IsBuyEstimate);
        Assert.Null(seg.SellPricePerKw);
    }

    [Fact]
    public void ApplyPrice_UsesAdvancedPredictedAsBuyPrice_WhenPresent()
    {
        var seg = NewSegment();
        var advanced = new AdvancedPrice { Low = 10m, Predicted = 18m, High = 30m };

        seg.ApplyPrice(new List<BaseInterval> { Current(ChannelType.General, perKwh: 25m, estimate: true, advanced: advanced) });

        Assert.Equal(18m, seg.BuyPricePerKw); // predicted, not perKwh
        Assert.Same(advanced, seg.AdvancedBuyPrice);
        Assert.True(seg.IsBuyEstimate);
    }

    [Fact]
    public void ApplyPrice_ControlledLoad_CountsAsBuyChannel()
    {
        var seg = NewSegment();

        seg.ApplyPrice(new List<BaseInterval> { Current(ChannelType.ControlledLoad, perKwh: 12m, estimate: false) });

        Assert.Equal(12m, seg.BuyPricePerKw);
    }

    [Fact]
    public void ApplyPrice_FeedIn_StoresNegatedSellPrice()
    {
        var seg = NewSegment();

        seg.ApplyPrice(new List<BaseInterval> { Forecast(ChannelType.FeedIn, perKwh: 8m) });

        Assert.Equal(-8m, seg.SellPricePerKw); // sell prices are stored negated
        Assert.Null(seg.BuyPricePerKw);
    }

    [Fact]
    public void ApplyPrice_SetsDemandWindowFromBuyInterval()
    {
        var seg = NewSegment();

        seg.ApplyPrice(new List<BaseInterval> { Current(ChannelType.General, perKwh: 25m, estimate: false, demandWindow: true) });

        Assert.True(seg.IsDemandWindow);
    }

    [Fact]
    public void ApplyPrice_PicksBuyIntervalWithGreatestOverlap()
    {
        var seg = NewSegment(); // [10:00, 10:05)
        var barelyOverlapping = Forecast(ChannelType.General, perKwh: 50m, start: SegStart.AddMinutes(-29)); // [09:31,10:01) -> 1 min
        var fullyCovering = Forecast(ChannelType.General, perKwh: 20m, start: SegStart);                     // [10:00,10:30) -> 5 min

        seg.ApplyPrice(new List<BaseInterval> { barelyOverlapping, fullyCovering });

        Assert.Equal(20m, seg.BuyPricePerKw); // chose the interval with the larger overlap
    }

    [Fact]
    public void ApplyPrice_IgnoresNonOverlappingIntervals()
    {
        var seg = NewSegment(); // [10:00, 10:05)
        var nonOverlapping = Forecast(ChannelType.General, perKwh: 99m, start: SegStart.AddHours(2));

        seg.ApplyPrice(new List<BaseInterval> { nonOverlapping });

        Assert.Null(seg.BuyPricePerKw);
    }
}
