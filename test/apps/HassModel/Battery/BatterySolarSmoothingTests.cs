using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using src.apps.HassModel.Battery;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Extensions;
using src.apps.HassModel.Battery.Models;
using Xunit;
using static Tests.apps.HassModel.Battery.BatteryTestData;

namespace Tests.apps.HassModel.Battery;

/// <summary>
/// Reproduces the buy/sell loop seen in production on 2026-06-14, where the deployed planner toggled
/// the battery action every few minutes while sitting near a capacity limit on solar.
///
/// Forecast.Solar reports energy in 15-minute periods, but the planner projects in 5-minute segments.
/// <see cref="EnergySegmentExtensions.ApplySolarForecast"/> summed only the forecast points landing
/// inside a segment, so a whole 15-minute period's energy was dumped into the single 5-minute segment
/// containing its timestamp and the other two got nothing. That turns a smooth solar ramp into a
/// sawtooth.
///
/// The controller re-plans every 5 minutes. Each cycle the "act now" segment (index 0) advances by one
/// 5-minute slot, so over three cycles it rotates through the three phases of the 15-minute forecast:
/// in one phase it receives the whole period's solar (a phantom up-spike), in the other two it receives
/// none. So the decision for *right now* flips purely on that phase alignment — export this cycle, idle
/// the next, export again — which is the loop observed in the logs. Smoothing each period evenly across
/// its segments removes the phantom spike and stabilises the decision.
/// </summary>
public class BatterySolarSmoothingTests
{
    // Usage of 0.1 kWh per 5-min segment (= 1.2 kWh/hr). A 300 Wh/15-min solar period smoothed across
    // three segments (0.1 kWh each) exactly cancels usage, so the TRUE trajectory is flat: no action is
    // warranted in any cycle, and any cycle-to-cycle change of action is pure churn.
    private const decimal SegmentUsage = 0.1m;
    private const decimal HourlyUsage = 1.2m;

    // A 15-minute-resolution forecast: one point per 15 minutes, exactly as Forecast.Solar returns it.
    private static Dictionary<DateTime, int> Forecast15Min(int periods, int whPerPeriod)
    {
        var dict = new Dictionary<DateTime, int>();
        for (var p = 0; p < periods; p++)
            dict[Base.AddMinutes(15 * p)] = whPerPeriod;
        return dict;
    }

    // Flat prices over a wide window so any planning offset is priced. Feed-in (5c) is below import
    // (30c), so no arbitrage is possible: every action is the boundary solver reacting to the trajectory.
    private static List<BaseInterval> FlatPrices(int slots)
    {
        var list = new List<BaseInterval>();
        for (var i = 0; i < slots; i++)
        {
            list.Add(GeneralInterval(i, 30m));
            list.Add(FeedInInterval(i, -5m)); // Amber reports feed-in as negative
        }
        return list;
    }

    // One planning cycle starting `offsetMin` into the day at `charge`; returns the action chosen for
    // "now" (segment 0), i.e. what the controller would command this cycle.
    private static EnergySegmentAction ActNowAction(int offsetMin, decimal charge, Dictionary<DateTime, int> solar, List<BaseInterval> prices)
    {
        var segs = BatteryPlanner.BuildSegments(Base.AddMinutes(offsetMin), charge, SegmentUsage, solar, prices, Cfg());
        BatteryPlanner.OptimiseSegments(segs, Cfg(), HourlyUsage);
        BatteryPlanner.ApplyArbitrage(segs, Cfg(), HourlyUsage);
        return segs[0].Action;
    }

    [Theory]
    [InlineData(49.95)] // just under Max: smoothed trajectory never crosses Max -> no action ever
    [InlineData(50.00)] // exactly at Max: smoothed stays flat at Max (solar in == usage out) -> no export
    public void ActNowDecision_IsStableAcrossForecastPhases_WhileFlatNearMax(double chargeArg)
    {
        var charge = (decimal)chargeArg;
        var solar = Forecast15Min(periods: 40, whPerPeriod: 300); // steady solar, smooths to == usage
        var prices = FlatPrices(slots: 200);

        // Three consecutive 5-minute control cycles. With an unchanged world the action must not change;
        // if it does, the controller is churning (the production buy/sell loop). Pre-fix the dumped solar
        // makes phase 0 read "at max and rising" and export while phases 1 and 2 idle.
        var actions = new[] { 0, 5, 10 }.Select(off => ActNowAction(off, charge, solar, prices)).ToList();

        var distinct = actions.Distinct().ToList();
        Assert.True(distinct.Count == 1,
            $"Action for 'now' flipped across consecutive cycles (buy/sell loop): " +
            string.Join(" -> ", actions.Select((a, i) => $"+{i * 5}min:{a}")));
    }

    [Fact]
    public void ApplySolarForecast_SpreadsFifteenMinutePeriodEvenlyAcrossSegments()
    {
        // The fix's mechanism: a 300 Wh period at 00:00 should appear as 0.1 kWh in EACH of the three
        // 5-min segments it spans ([00:00,00:05),[00:05,00:10),[00:10,00:15)), not 0.3 kWh in the first.
        var solar = Forecast15Min(periods: 2, whPerPeriod: 300); // 2nd point only defines the period length

        var seg0 = MakeSegment(0);
        var seg1 = MakeSegment(1);
        var seg2 = MakeSegment(2);
        seg0.ApplySolarForecast(solar);
        seg1.ApplySolarForecast(solar);
        seg2.ApplySolarForecast(solar);

        Assert.Equal(0.1m, seg0.SolarForecastKwh, precision: 3);
        Assert.Equal(0.1m, seg1.SolarForecastKwh, precision: 3);
        Assert.Equal(0.1m, seg2.SolarForecastKwh, precision: 3);
    }

    private static EnergySegment MakeSegment(int index) => new()
    {
        Duration = TimeSpan.FromMinutes(5),
        StartUtc = Base.AddMinutes(5 * index),
        EstimatedBatteryChargeKwh = 0m
    };
}
