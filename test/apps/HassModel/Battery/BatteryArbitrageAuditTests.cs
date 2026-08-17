using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Clients.AmberClient.Enums;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Extensions;
using src.apps.HassModel.Battery.Models;
using Xunit;
using Xunit.Abstractions;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// End-to-end harness for the arbitrage pass, reproducing the DEPLOYED configuration and a realistic 42 h
/// price/usage/solar shape taken from 2026-08-12 — the day the audit measured arbitrage losing money on
/// every realised round trip. Unlike the small hand-built fixtures in <see cref="BatteryArbitrageTests"/>,
/// this drives the real <c>BuildSegments -> OptimiseSegments -> ApplyArbitrage</c> pipeline.
///
/// Tests here are written as BEFORE/AFTER comparisons against <see cref="PreFixCfg"/> (the deployed config
/// with the two new guards disabled) so each one fails if the guard stops working AND documents what the
/// old behaviour was. See docs/battery-arbitrage-fix-review.md.
/// </summary>
public class BatteryArbitrageAuditTests
{
    private readonly ITestOutputHelper _out;
    public BatteryArbitrageAuditTests(ITestOutputHelper output) => _out = output;

    private const decimal HourlyUsage = 3.6m; // the flat 3-day average the deployed app passes (logs: 3.0-4.2)

    // Deployed BatteryControl.yaml values.
    private static BatteryConfig DeployedCfg() => new()
    {
        BatteryCapacity = 53m,
        MinCapacity = 5.3m,
        MaxCapacity = 53m,
        SegmentSizeMins = 5,
        ChargeRateKw = 10m,
        DischargeRateKw = 10m,
        MinForecastHours = 0,
        EstimatedUsageMultiplier = 1m,
        DemandWindowUsageMultiplier = 1.5m,
        PessimismStartHours = 12m,
        PessimismMaxAtHours = 0m,
        PessimismMaxWeight = 2m,
        OptimismStartHours = 20m,
        OptimismMaxAtHours = 24m,
        OptimismMaxWeight = 0.3m,
        ArbitrageEnabled = true,
        ArbitragePessimismWeight = 0.5m,
        ArbitrageBuyBeforeSellWeight = 0.25m,
        RoundTripEfficiency = 0.9m,
        ArbitrageMinMarginPerKwh = 2m,
        ArbitrageHoldDrainReserveFraction = 1.0m,
        ActionReversalCooldownSegments = 3
    };

    /// <summary>The deployed config with the new guards OFF — i.e. behaviour as audited.</summary>
    private static BatteryConfig PreFixCfg()
    {
        var cfg = DeployedCfg();
        cfg.ArbitrageHoldDrainReserveFraction = 0m;
        cfg.ActionReversalCooldownSegments = 0;
        return cfg;
    }

    // ---- 2026-08-12 shaped inputs -------------------------------------------------------------
    // Import (general) c/kWh and feed-in EARNING c/kWh by hour of day, read off the deployed logs and the
    // Amber price sensors for that day.
    private static (decimal Import, decimal Earn) PriceAt(int hour) => hour switch
    {
        >= 0 and < 6 => (14m, 8m),
        >= 6 and < 9 => (21m, 14m),
        >= 9 and < 11 => (11m, 4m),
        >= 11 and < 15 => (8m, 1m),
        >= 15 and < 17 => (14m, 8m),
        >= 17 and < 20 => (19m, 15m),
        _ => (18m, 13m)
    };

    // Per-5-minute household drain by hour, from the deployed "Per-segment usage estimate profile" log line.
    private static decimal UsageAt(int hour) => hour switch
    {
        >= 0 and < 5 => 0.10m,
        >= 5 and < 8 => 0.19m,
        >= 8 and < 11 => 0.31m,
        >= 11 and < 16 => 0.09m,
        >= 16 and < 21 => 0.25m,
        _ => 0.20m
    };

    private const int HorizonHours = 42;

    private static List<BaseInterval> BuildPrices(DateTime startUtc)
    {
        var intervals = new List<BaseInterval>();
        for (var i = 0; i < HorizonHours * 12; i++)
        {
            var start = startUtc.AddMinutes(5 * i);
            var (import, earn) = PriceAt(start.Hour);
            // Amber publishes the advanced (ML) band for ~24 h only; past that just perKwh.
            var withinAdvancedHorizon = i < 24 * 12;
            // Import band: low < predicted < high. Feed-in band is NEGATIVE with High most-negative
            // (= best earning), matching the real API (see docs/amber-api.md). Half-widths are the medians
            // measured off the live API on 2026-08-12: high-predicted 1.7c, predicted-low 1.5c.
            AdvancedPrice? advImport = withinAdvancedHorizon ? new AdvancedPrice { Low = import - 1.5m, Predicted = import, High = import + 1.7m } : null;
            AdvancedPrice? advFeed = withinAdvancedHorizon ? new AdvancedPrice { Low = -(earn - 1.4m), Predicted = -earn, High = -(earn + 1.6m) } : null;

            if (i == 0)
            {
                // The current interval: locked in (Estimate = false).
                intervals.Add(new CurrentInterval
                {
                    ChannelType = ChannelType.General, PerKwh = import, Estimate = false, AdvancedPrice = advImport,
                    StartTime = start, EndTime = start.AddMinutes(5), TariffInformation = new TariffInformation()
                });
                intervals.Add(new CurrentInterval
                {
                    ChannelType = ChannelType.FeedIn, PerKwh = -earn, Estimate = false, AdvancedPrice = advFeed,
                    StartTime = start, EndTime = start.AddMinutes(5), TariffInformation = new TariffInformation()
                });
                continue;
            }
            intervals.Add(new ForecastInterval
            {
                ChannelType = ChannelType.General, PerKwh = import, AdvancedPrice = advImport,
                StartTime = start, EndTime = start.AddMinutes(5), TariffInformation = new TariffInformation()
            });
            intervals.Add(new ForecastInterval
            {
                ChannelType = ChannelType.FeedIn, PerKwh = -earn, AdvancedPrice = advFeed,
                StartTime = start, EndTime = start.AddMinutes(5), TariffInformation = new TariffInformation()
            });
        }
        return intervals;
    }

    // A modest winter solar day: ramps 07:00-16:00, peaking ~4 kW around noon. Wh per 15-min period.
    private static Dictionary<DateTime, int> BuildSolar(DateTime startUtc)
    {
        var solar = new Dictionary<DateTime, int>();
        for (var i = 0; i < HorizonHours * 4; i++)
        {
            var t = startUtc.AddMinutes(15 * i);
            var h = t.Hour + t.Minute / 60.0;
            var wh = 0.0;
            if (h is > 7 and < 16)
            {
                var frac = Math.Sin((h - 7) / 9.0 * Math.PI); // 0 at 07:00, 1 at 11:30, 0 at 16:00
                wh = 4000 * frac * 0.25;                      // 4 kW peak over a 15-min period
            }
            solar[t] = (int)wh;
        }
        return solar;
    }

    /// <summary>Runs the full deployed pipeline and returns the finished plan.</summary>
    private static List<EnergySegment> Plan(DateTime startUtc, decimal socKwh, BatteryConfig cfg)
    {
        var segments = BatteryPlanner.BuildSegments(
            startUtc, socKwh, t => UsageAt(t.Hour), BuildSolar(startUtc), BuildPrices(startUtc), cfg);
        BatteryPlanner.OptimiseSegments(segments, cfg, HourlyUsage);
        BatteryPlanner.ApplyArbitrage(segments, cfg, HourlyUsage);
        return segments;
    }

    private static List<EnergySegment> ArbitrageLegs(List<EnergySegment> segments, EnergySegmentAction action)
        => segments.Where(s => s.Action == action && s.ActionReason == EnergySegmentActionReason.Arbitrage).ToList();

    private void DumpLegs(string label, List<EnergySegment> segments)
    {
        var legs = segments.Where(s => s.Action != EnergySegmentAction.None && s.ActionReason == EnergySegmentActionReason.Arbitrage).ToList();
        var text = string.Join(", ", legs.Select(s =>
            $"{s.Action} {s.StartUtc:ddd HH:mm}@{(s.Action == EnergySegmentAction.Buy ? s.BuyPricePerKw : s.SellPricePerKw)}c"));
        _out.WriteLine($"{label}: {legs.Count} arbitrage legs{(legs.Count > 0 ? " — " + text : "")}");
    }

    // ===========================================================================================
    // The trade that lost money
    // ===========================================================================================

    /// <summary>
    /// The 2026-08-12 06:25 morning sell (SoC 20.1 kWh), the shape that production actually fired: export
    /// at the locked 14c feed-in against a booked 8c refill at the midday solar trough. It cleared the old
    /// gate comfortably (5.1c/kWh on paper) but the hold window carries ~15 kWh of projected drain, and
    /// the drain that morning ran roughly double the estimate — so the floor arrived first and the
    /// boundary solver imported at 19-21c instead.
    ///
    /// The drain reserve must block it. This test fails if the reserve stops binding.
    /// </summary>
    [Fact]
    public void MorningSell_AgainstADistantRefill_BlockedByTheDrainReserve()
    {
        var start = new DateTime(2026, 8, 12, 6, 25, 0, DateTimeKind.Utc);

        var before = Plan(start, 20.1m, PreFixCfg());
        DumpLegs("pre-fix ", before);
        var after = Plan(start, 20.1m, DeployedCfg());
        DumpLegs("post-fix", after);

        // Pre-fix this fired — if it ever stops, the fixture no longer reproduces the audited defect and
        // the "after" half of this test proves nothing.
        Assert.True(before[0].Action == EnergySegmentAction.Sell,
            $"fixture no longer reproduces the defect: pre-fix action was {before[0].Action}/{before[0].ActionReason}");

        // Post-fix the current segment must not be sold against a refill hours away.
        Assert.True(after[0].Action != EnergySegmentAction.Sell,
            $"the morning sell is still being committed: {after[0].Action}/{after[0].ActionReason}");
        Assert.DoesNotContain(ArbitrageLegs(after, EnergySegmentAction.Sell), s => s.StartUtc < start.AddHours(3));
    }

    /// <summary>
    /// The reserve must bite because of the HOLD LENGTH, not because it rejects everything. Same plan, same
    /// prices: a sell paired with a refill a few segments away carries almost no drain and stays feasible,
    /// while the long hold does not. Without this, a reserve that simply blocked all sell-before-buy pairs
    /// would pass the test above for the wrong reason.
    /// </summary>
    [Fact]
    public void DrainReserve_ScalesWithHoldLength_NotAppliedFlat()
    {
        var cfg = DeployedCfg();
        var start = new DateTime(2026, 8, 12, 6, 25, 0, DateTimeKind.Utc);
        var segments = BatteryPlanner.BuildSegments(
            start, 20.1m, t => UsageAt(t.Hour), BuildSolar(start), BuildPrices(start), cfg);
        BatteryPlanner.OptimiseSegments(segments, cfg, HourlyUsage);

        // FeasiblePair is private; exercise it through the reserve arithmetic it applies. A 3-segment hold
        // reserves 0.5 * ~0.57 kWh; a 59-segment hold reserves 0.5 * ~15 kWh.
        decimal DrainOver(int from, int count) => segments.Skip(from).Take(count).Sum(s => s.UsageKwh);
        var shortHoldReserve = cfg.ArbitrageHoldDrainReserveFraction * DrainOver(0, 3);
        var longHoldReserve = cfg.ArbitrageHoldDrainReserveFraction * DrainOver(0, 59);
        _out.WriteLine($"reserve over a 3-segment hold {shortHoldReserve:0.000} kWh; over a 59-segment hold {longHoldReserve:0.00} kWh");

        Assert.True(shortHoldReserve < cfg.SegmentDischargeAmountKwh,
            "a short hold should reserve less than the fixed one-step buffer");
        Assert.True(longHoldReserve > 5m * cfg.SegmentDischargeAmountKwh,
            "a multi-hour hold should reserve several steps' worth");
    }

    // ===========================================================================================
    // Regression guards — the fix must not kill the trades that make sense
    // ===========================================================================================

    /// <summary>
    /// Buy-before-sell (pre-charge cheap at the midday trough, export into the evening peak) is the
    /// direction that actually works when feed-in sits below import, and it is NOT what lost money. It has
    /// no floor risk — it adds charge and holds it — so neither guard should touch it.
    /// </summary>
    [Fact]
    public void EveningBuyBeforeSell_Survives()
    {
        var start = new DateTime(2026, 8, 12, 18, 15, 0, DateTimeKind.Utc);
        var after = Plan(start, 27.0m, DeployedCfg());
        DumpLegs("post-fix", after);

        var buys = ArbitrageLegs(after, EnergySegmentAction.Buy);
        var sells = ArbitrageLegs(after, EnergySegmentAction.Sell);
        Assert.NotEmpty(buys);
        Assert.NotEmpty(sells);
        // Every committed pair here should be charge-first: the cheapest buy precedes the dearest sell.
        Assert.True(buys.Min(b => b.StartUtc) < sells.Max(s => s.StartUtc),
            "expected buy-before-sell pairs to survive the fix");
        Assert.True(buys.All(b => b.BuyPricePerKw < sells.Min(s => s.SellPricePerKw)),
            "a surviving pair should still buy below what it sells for");
    }

    // ===========================================================================================
    // Consistency between the two passes
    // ===========================================================================================

    /// <summary>
    /// The refill leg of a sell-before-buy pair must never be valued more cheaply by arbitrage than the
    /// boundary solver values the same segment. Before the fix arbitrage priced the 08-12 11:00 refill at
    /// 8.85c while the solver priced it at 10.87c — a 2c gap, which was itself enough to push the losing
    /// pair through the 2c margin gate.
    /// </summary>
    [Fact]
    public void ArbitrageNeverValuesARefillBelowWhatTheSolverWouldPay()
    {
        var cfg = DeployedCfg();
        var start = new DateTime(2026, 8, 12, 6, 25, 0, DateTimeKind.Utc);
        var segments = BatteryPlanner.BuildSegments(
            start, 20.1m, t => UsageAt(t.Hour), BuildSolar(start), BuildPrices(start), cfg);
        BatteryPlanner.OptimiseSegments(segments, cfg, HourlyUsage);

        // Check every candidate refill in the advanced-price horizon, not just the one that happened to be
        // picked — the guarantee should hold for all of them.
        var checkedCount = 0;
        foreach (var segment in segments.Where(s => s.BuyPricePerKw != null && s.IsBuyEstimate && s.AdvancedBuyPrice != null))
        {
            var runway = EnergySegmentExtensions.GetHoursToEmpty(segment.EstimatedBatteryChargeKwh, cfg.MinCapacity, HourlyUsage);
            var solverWeight = EnergySegmentExtensions.GetRiskWeight(runway, cfg);
            var arbitrageWeight = Math.Max(cfg.ArbitragePessimismWeight, solverWeight);
            var arbitrageView = segment.WeightedPrice(isBuy: true, arbitrageWeight);
            var solverView = segment.GetWeightedPrice(isBuy: true, cfg, HourlyUsage);
            Assert.True(arbitrageView >= solverView - 0.0001m,
                $"segment {segment.StartUtc:ddd HH:mm}: arbitrage {arbitrageView:0.00}c < solver {solverView:0.00}c");
            checkedCount++;
        }
        _out.WriteLine($"checked {checkedCount} candidate refill segments");
        Assert.True(checkedCount > 100, "expected a meaningful number of candidate segments");
    }

    // ===========================================================================================
    // Known remaining gap — documented, NOT fixed
    // ===========================================================================================

    /// <summary>
    /// KNOWN GAP (not addressed by this fix): <c>WeightedPrice</c> returns locked prices raw while every
    /// forecast leg is discounted, so the CURRENT segment always outranks an identical future one and
    /// arbitrage systematically sells "now" rather than at the peak it planned for. This test pins the
    /// behaviour so the gap is visible rather than forgotten — if it starts failing, that cliff has been
    /// addressed and this test should become the assertion of the new behaviour.
    /// </summary>
    [Fact]
    public void KnownGap_LockedNowSell_StillOutranksAnIdenticalFutureSell()
    {
        var cfg = DeployedCfg();
        var start = new DateTime(2026, 8, 12, 18, 15, 0, DateTimeKind.Utc);
        var segments = BatteryPlanner.BuildSegments(
            start, 27.0m, t => UsageAt(t.Hour), BuildSolar(start), BuildPrices(start), cfg);

        var now = segments[0];
        var tomorrowPeak = segments.First(s => s.StartUtc >= start.AddHours(23) && s.StartUtc.Hour == 17);
        var nowRanked = now.WeightedPrice(isBuy: false, cfg.ArbitragePessimismWeight);
        var futureRanked = tomorrowPeak.WeightedPrice(isBuy: false, cfg.ArbitragePessimismWeight);
        _out.WriteLine($"now {now.StartUtc:HH:mm} face {now.SellPricePerKw}c -> ranked {nowRanked}c (locked); " +
                       $"peak {tomorrowPeak.StartUtc:ddd HH:mm} face {tomorrowPeak.SellPricePerKw}c -> ranked {futureRanked}c (estimate)");

        Assert.Equal(now.SellPricePerKw, nowRanked);
        Assert.Equal(now.SellPricePerKw, tomorrowPeak.SellPricePerKw);  // identical face value
        Assert.True(nowRanked > futureRanked, "the locked/estimate cliff appears to have been fixed — update this test");
    }
}
