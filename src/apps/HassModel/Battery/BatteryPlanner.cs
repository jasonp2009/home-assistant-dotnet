using System;
using System.Collections.Generic;
using System.Linq;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;
using src.apps.HassModel.Battery.Enums;
using src.apps.HassModel.Battery.Extensions;
using src.apps.HassModel.Battery.Models;

namespace src.apps.HassModel.Battery;

/// <summary>
/// Pure battery planning logic with no Home Assistant dependencies, so it can be unit tested.
/// <see cref="BatteryControl"/> reads/writes Home Assistant and delegates the decision making here.
/// </summary>
public static class BatteryPlanner
{
    /// <summary>
    /// Projects a list of <see cref="EnergySegment"/>s forward from the current charge, applying the
    /// per-segment household usage (drain) and the solar forecast (charge), and tagging each segment
    /// with its Amber buy/sell prices. Segments are produced until prices run out and the minimum
    /// forecast horizon has been reached.
    /// </summary>
    public static List<EnergySegment> BuildSegments(
        DateTime startUtc,
        decimal currentChargeKwh,
        decimal averageSegmentUsage,
        Dictionary<DateTime, int>? solarForecast,
        List<BaseInterval>? amberPrices,
        BatteryConfig config)
        => BuildSegments(startUtc, currentChargeKwh, _ => averageSegmentUsage, solarForecast, amberPrices, config);

    /// <summary>
    /// As above, but drains each segment by a per-segment usage estimate. <paramref name="segmentUsage"/>
    /// is queried with the UTC start of the segment being drained (the elapsed interval), so a
    /// time-of-day estimate produces a realistically shaped trajectory (e.g. evening peaks, overnight
    /// flats) rather than a single flat drain.
    /// </summary>
    public static List<EnergySegment> BuildSegments(
        DateTime startUtc,
        decimal currentChargeKwh,
        Func<DateTime, decimal> segmentUsage,
        Dictionary<DateTime, int>? solarForecast,
        List<BaseInterval>? amberPrices,
        BatteryConfig config)
    {
        var curEnergySegment = new EnergySegment
        {
            EstimatedBatteryChargeKwh = currentChargeKwh,
            Duration = config.SegmentSize,
            StartUtc = startUtc,
            UsageKwh = segmentUsage(startUtc)
        };
        curEnergySegment.ApplySolarForecast(solarForecast);
        curEnergySegment.ApplyPrice(amberPrices);
        ApplyDemandWindowUsage(curEnergySegment, config);
        var energySegments = new List<EnergySegment>
        {
            curEnergySegment
        };
        while (curEnergySegment.BuyPricePerKw is not null ||
               curEnergySegment.SellPricePerKw is not null ||
               curEnergySegment.StartUtc < startUtc + TimeSpan.FromHours(config.MinForecastHours))
        {
            var nextStartUtc = curEnergySegment.StartUtc + config.SegmentSize;
            curEnergySegment = new EnergySegment
            {
                // Drain by the previous segment's UsageKwh (not a fresh segmentUsage call) so any
                // demand-window multiplier already applied to it carries into the charge trajectory.
                EstimatedBatteryChargeKwh = curEnergySegment.EstimatedBatteryChargeKwh - curEnergySegment.UsageKwh,
                Duration = config.SegmentSize,
                StartUtc = nextStartUtc,
                UsageKwh = segmentUsage(nextStartUtc)
            };
            curEnergySegment.ApplySolarForecast(solarForecast);
            curEnergySegment.ApplyPrice(amberPrices);
            ApplyDemandWindowUsage(curEnergySegment, config);
            energySegments.Add(curEnergySegment);
        }
        return energySegments;
    }

    /// <summary>
    /// Rescales a demand-window segment's <see cref="EnergySegment.UsageKwh"/> to use
    /// <c>DemandWindowUsageMultiplier</c> in place of <c>EstimatedUsageMultiplier</c>, inflating the
    /// projected drain so the plan reserves more charge through the window (avoiding a forced grid
    /// import if load spikes). Must run after <c>ApplyPrice</c>, which sets <c>IsDemandWindow</c>. The
    /// per-segment usage already carries <c>EstimatedUsageMultiplier</c>, so divide it out first. A
    /// non-positive <c>DemandWindowUsageMultiplier</c> (unset) leaves the usage unchanged.
    /// </summary>
    private static void ApplyDemandWindowUsage(EnergySegment segment, BatteryConfig config)
    {
        if (!segment.IsDemandWindow) return;
        if (config.DemandWindowUsageMultiplier <= 0m || config.EstimatedUsageMultiplier <= 0m) return;
        segment.UsageKwh = segment.UsageKwh / config.EstimatedUsageMultiplier * config.DemandWindowUsageMultiplier;
    }

    /// <summary>
    /// Greedy solver: while the projected charge crosses a capacity limit, mark the best-priced
    /// segment in the affected window as Sell (above max) or Buy (below min), re-simulate the charge
    /// forward, and repeat until the projection stays within [MinCapacity, MaxCapacity].
    /// Mutates <paramref name="energySegments"/> in place (Action and EstimatedBatteryChargeKwh).
    ///
    /// The limits are detected (<see cref="CalculateBoundaryResult"/>) at MinCapacity/MaxCapacity, but a
    /// solver action reserves a ONE-STEP buffer: a buy may charge only up to MaxCapacity - one segment,
    /// and a sell may discharge only down to MinCapacity + one segment. Otherwise a charge could land the
    /// projection exactly on MaxCapacity (the sell trigger) and a discharge exactly on MinCapacity (the
    /// buy trigger), so a single action would directly arm the opposite action a step later — the
    /// buy/sell loop. With the buffer the battery can reach a trigger only from solar (over max) or usage
    /// (under min), never from the solver's own action.
    /// </summary>
    public static void OptimiseSegments(List<EnergySegment> energySegments, BatteryConfig config, decimal hourlyUsage)
    {
        var boundaryResult = CalculateBoundaryResult(energySegments, config);
        var loopCount = 0;
        while (boundaryResult.IsOutOfBounds && loopCount < energySegments.Count)
        {
            var previousBoundaryCrossingIndex = GetPreviousBoundaryCrossingIndex(energySegments, boundaryResult, config);
            loopCount++;
            if (boundaryResult.IsMax == true)
            {
                var maxPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        // Post-sell level subsumes this segment's natural flow (see the apply step below),
                        // so predict it the same way to keep the one-step buffer above Min intact.
                        config.MinCapacity + config.SegmentDischargeAmountKwh <= (segment.EstimatedBatteryChargeKwh - config.SegmentDischargeAmountKwh - segment.NaturalChargeDeltaKwh) &&
                        previousBoundaryCrossingIndex <= index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing)
                    .MaxBy(segment => segment.GetWeightedPrice(false, config, hourlyUsage));
                if (maxPriceSegment is null)
                    break;
                var maxPriceSegmentIndex = energySegments.IndexOf(maxPriceSegment);
                maxPriceSegment.Action = EnergySegmentAction.Sell;
                maxPriceSegment.ActionReason = EnergySegmentActionReason.Usage;
                // Forcing discharge caps the battery's net change for this segment at the inverter's rate,
                // so the segment's own natural flow is subsumed (its solar exports and its load is served
                // from the discharge, both within the cap). The projection already baked that flow in, so
                // remove it: a high-usage segment now drops by exactly the discharge amount, not amount + usage.
                var dischargeDelta = config.SegmentDischargeAmountKwh + maxPriceSegment.NaturalChargeDeltaKwh;
                for (var i = maxPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh -= dischargeDelta;
                }
            }
            if (boundaryResult.IsMax == false)
            {
                var lowestPriceSegment = energySegments
                    .Where((segment, index) =>
                        segment.Action is EnergySegmentAction.None &&
                        // Post-buy level subsumes this segment's natural flow (see the apply step below),
                        // so predict it the same way to keep the one-step buffer below Max intact.
                        (segment.EstimatedBatteryChargeKwh + config.SegmentChargeAmountKwh - segment.NaturalChargeDeltaKwh) <= config.MaxCapacity - config.SegmentChargeAmountKwh &&
                        previousBoundaryCrossingIndex <= index &&
                        index <= boundaryResult.IndexOfBoundaryCrossing &&
                        !segment.IsDemandWindow)
                    .MinBy(segment => segment.GetWeightedPrice(true, config, hourlyUsage));
                if (lowestPriceSegment is null)
                    break;
                var lowestPriceSegmentIndex = energySegments.IndexOf(lowestPriceSegment);
                lowestPriceSegment.Action = EnergySegmentAction.Buy;
                lowestPriceSegment.ActionReason = EnergySegmentActionReason.Usage;
                // Forcing charge caps the battery's net change for this segment at the inverter's rate, so
                // the segment's own natural flow is subsumed (its solar surplus and load are met within the
                // cap via the grid). The projection already baked that flow in, so remove it: a sunny segment
                // now rises by exactly the charge amount, not amount + solar surplus.
                var chargeDelta = config.SegmentChargeAmountKwh - lowestPriceSegment.NaturalChargeDeltaKwh;
                for (var i = lowestPriceSegmentIndex; i < energySegments.Count; i++)
                {
                    energySegments[i].EstimatedBatteryChargeKwh += chargeDelta;
                }
            }
            boundaryResult = CalculateBoundaryResult(energySegments, config);
        }
    }

    /// <summary>
    /// Opportunistic price arbitrage (buy import low / export high). Runs AFTER <see cref="OptimiseSegments"/>
    /// on the same segment list and mutates it in place. No-op when <c>config.ArbitrageEnabled</c> is false.
    ///
    /// Greedy loop:
    ///  1. Among segments with <c>Action == None</c> and a sell price (<c>SellPricePerKw != null</c>), take the
    ///     one with the highest pessimistic (estimate-discounted) earning.
    ///  2. Among segments with <c>Action == None</c> and a buy price (<c>BuyPricePerKw != null</c>), other than
    ///     the chosen sell, that form a FEASIBLE pair, take the one giving the best net profit per kWh.
    ///  3. Commit the pair only when it clears the profit gate:
    ///       <c>net := sellEarning - buyCost / config.RoundTripEfficiency >= config.ArbitrageMinMarginPerKwh</c>.
    ///     Committing sets <c>buy.Action = Buy</c>, <c>sell.Action = Sell</c>, and applies the 1 kWh round-trip to
    ///     the projection: <c>+SegmentChargeAmountKwh</c> from the buy index onward and
    ///     <c>-SegmentDischargeAmountKwh</c> from the sell index onward (same convention as OptimiseSegments).
    ///  4. Repeat until no profitable, feasible pair remains. If the best sell has no profitable feasible buy,
    ///     move on to the next-best sell before stopping (don't give up at the first miss).
    ///
    /// PESSIMISM IS ESTIMATE-BASED: the discount is applied to any leg whose price is an ESTIMATE (a forecast)
    /// — leaning a buy toward its advanced High and a sell toward its lowest plausible earning — while a LOCKED
    /// (materialised) leg passes through at face value (handled by <see cref="EnergySegmentExtensions.WeightedPrice"/>).
    /// So an uncertain future price is acted on only when even its pessimistic value clears the gate: the planner
    /// won't skip a certain good price now to chase a higher-but-uncertain forecast. Both legs of an all-forecast
    /// pair are discounted; a pair anchored by the locked current segment keeps that leg at face value. (Pricing
    /// the speculative leg at its worst plausible value depends on the sell-side lean being correct — see
    /// <see cref="EnergySegmentExtensions.WeightedPrice"/>.)
    ///
    /// PESSIMISM IS DIRECTIONAL: buy-before-sell pairs (charge now, export later) use the lower
    /// <c>config.ArbitrageBuyBeforeSellWeight</c>; sell-before-buy pairs (discharge now, refill later) use the full
    /// <c>config.ArbitragePessimismWeight</c>. Charging is low-regret — if the forecast sell never materialises the
    /// bought energy still displaces household load — whereas a sell-before-buy that misjudges the refill leaves the
    /// battery short and forces a dearer top-up, so it is treated more cautiously. This lets the planner pre-charge
    /// cheap for a several-hours-ahead peak that the symmetric weight would have gated out.
    ///
    /// FEASIBILITY keeps the round-trip within [MinCapacity, MaxCapacity] (i.e. inside the slack between
    /// boundary crossings), using a small tolerance (e.g. 0.01) to absorb SegmentChargeAmountKwh rounding:
    ///  - buy before sell (charge cheap, hold, export dear): every segment i with buyIndex &lt;= i &lt; sellIndex
    ///    must satisfy <c>charge[i] + SegmentChargeAmountKwh &lt;= MaxCapacity + tol</c>.
    ///  - sell before buy (export from stored charge, refill cheap): every segment i with sellIndex &lt;= i &lt; buyIndex
    ///    must satisfy <c>charge[i] - SegmentDischargeAmountKwh &gt;= MinCapacity - tol</c>.
    ///
    /// Round-trip efficiency is applied ONLY in the profit gate, not in the 1 kWh charge accounting.
    /// </summary>
    public static void ApplyArbitrage(List<EnergySegment> energySegments, BatteryConfig config, decimal hourlyUsage)
    {
        if (!config.ArbitrageEnabled) return;
        const decimal tol = 0.01m;
        var loopGuard = 0;
        while (loopGuard++ < energySegments.Count)
        {
            // NOTE the asymmetry with the pair pricing below: ranking happens before a direction is known,
            // so it always applies the runway-aware LegWeight, while the pair itself only applies it to
            // sell-before-buy. A sell sitting on a low-SoC segment is therefore de-prioritised even if it
            // ends up in a buy-before-sell pair that prices it at the flat weight. This affects the ORDER
            // sells are tried in, never whether a pair is admissible, and de-prioritising a sell when the
            // battery is projected low is the conservative direction — but it is an asymmetry, not an
            // accident, and it is why the two calls do not read identically.
            //
            // Rank candidate sells (Action==None, SellPricePerKw != null) by pessimistic earning, DESC: an
            // estimate sell is discounted toward its lowest plausible earning, a locked sell is at face value
            // (WeightedPrice passthrough). That stops a higher-but-uncertain forecast outranking a certain price
            // now. Un-actionable legs (estimates past Amber's advanced horizon, for which WeightedPrice returns
            // the decimal.Min/MaxValue sentinel) are excluded: the net calculation below does arithmetic on the
            // leg price, which would overflow the decimal range.
            var sells = energySegments
                .Where(s => s.Action == EnergySegmentAction.None && s.SellPricePerKw != null && HasActionableSellPrice(s))
                .OrderByDescending(s => s.WeightedPrice(isBuy: false, LegWeight(s, config.ArbitragePessimismWeight, config, hourlyUsage)));

            var committed = false;
            foreach (var sell in sells)
            {
                var sellIndex = energySegments.IndexOf(sell);

                // Candidate buys: Action==None, BuyPricePerKw != null, not the sell, not a demand window
                // (buying in a demand window incurs extra charges, so it's disallowed — selling is fine),
                // and the pair is feasible. Pessimise each ESTIMATE leg (locked legs pass through at face via
                // WeightedPrice); keep the buy with the best net.
                EnergySegment? bestBuy = null;
                var bestNet = decimal.MinValue;
                foreach (var buy in energySegments.Where(b => b.Action == EnergySegmentAction.None && b.BuyPricePerKw != null && b != sell && !b.IsDemandWindow && HasActionableBuyPrice(b)))
                {
                    var buyIndex = energySegments.IndexOf(buy);
                    if (!FeasiblePair(buyIndex, sellIndex, energySegments, config, tol)) continue;
                    // Direction picks the pessimism: buy-before-sell (charge first) is low-regret and uses the
                    // lower ArbitrageBuyBeforeSellWeight; sell-before-buy (discharge first) keeps the full
                    // ArbitragePessimismWeight. Both legs of the pair share the chosen weight (see remarks).
                    // The runway markup applies only to SELL-BEFORE-BUY: that is the direction whose premise
                    // is a refill that has not happened yet, so it must be priced the way the boundary solver
                    // prices the same segment. Buy-before-sell keeps the flat weight — it is pre-charging, and
                    // if the export never materialises the energy still displaces load. See LegWeight.
                    var buyBeforeSell = buyIndex < sellIndex;
                    var weight = buyBeforeSell ? config.ArbitrageBuyBeforeSellWeight : config.ArbitragePessimismWeight;
                    var sellEarning = sell.WeightedPrice(isBuy: false, buyBeforeSell ? weight : LegWeight(sell, weight, config, hourlyUsage));
                    var buyCost = buy.WeightedPrice(isBuy: true, buyBeforeSell ? weight : LegWeight(buy, weight, config, hourlyUsage));
                    var net = sellEarning - buyCost / config.RoundTripEfficiency;
                    if (net > bestNet) { bestNet = net; bestBuy = buy; }
                }
                if (bestBuy is null) continue; // This sell has no feasible buy -> try next-best sell

                // Profit gate: net profit per kWh must clear the configured margin.
                if (bestNet >= config.ArbitrageMinMarginPerKwh)
                {
                    var buyIndex = energySegments.IndexOf(bestBuy);
                    bestBuy.Action = EnergySegmentAction.Buy;
                    bestBuy.ActionReason = EnergySegmentActionReason.Arbitrage;
                    sell.Action = EnergySegmentAction.Sell;
                    sell.ActionReason = EnergySegmentActionReason.Arbitrage;
                    // Each forced leg moves the battery by exactly the inverter's per-segment rate; the leg
                    // segment's own natural solar/usage flow is subsumed by that move, so remove it from the
                    // applied delta (it is already in the projection). Same convention as OptimiseSegments.
                    var buyDelta = config.SegmentChargeAmountKwh - bestBuy.NaturalChargeDeltaKwh;
                    var sellDelta = config.SegmentDischargeAmountKwh + sell.NaturalChargeDeltaKwh;
                    for (var i = buyIndex; i < energySegments.Count; i++) energySegments[i].EstimatedBatteryChargeKwh += buyDelta;
                    for (var i = sellIndex; i < energySegments.Count; i++) energySegments[i].EstimatedBatteryChargeKwh -= sellDelta;
                    committed = true;
                    break; // Restart the outer while from scratch
                }
                // else: this sell's best buy isn't profitable -> try the next-best sell
            }
            if (!committed) break; // No profitable feasible pair anywhere -> done
        }
    }

    /// <summary>
    /// Anti-thrash guard applied to the action actually sent to the inverter. Returns
    /// <see cref="EnergySegmentAction.None"/> when <paramref name="proposed"/> is the OPPOSITE of the last
    /// committed action and fewer than <c>ActionReversalCooldownSegments</c> segments have passed since it;
    /// otherwise returns <paramref name="proposed"/> unchanged.
    ///
    /// This is deliberately an ACTUATION-level rule, not a planning one: the planner re-derives the whole
    /// plan from scratch every 5 minutes and holds no memory, so the oscillation it produces (production
    /// showed Buy 25c -> Sell 24c -> Buy 26c on consecutive segments, and sell/none/sell/none fragmentation)
    /// cannot be seen from inside a single plan. Keeping the rule here leaves <see cref="BatteryPlanner"/>
    /// pure — the caller owns the "what did I do last" state.
    ///
    /// Two asymmetries, both chosen so the guard can only ever be SAFE:
    ///  - It only ever downgrades to None. It never forces an action to continue, so it cannot keep
    ///    discharging a battery the planner has decided to stop discharging.
    ///  - A Usage (floor-defence) BUY is never blocked. Refusing to charge can strand the battery on the
    ///    floor and force a dearer import later, so a mandatory buy always wins over the cooldown; only
    ///    discretionary moves (any Sell, or an Arbitrage buy) are suppressible.
    /// </summary>
    public static EnergySegmentAction ApplyActionReversalCooldown(
        EnergySegmentAction proposed,
        EnergySegmentActionReason proposedReason,
        EnergySegmentAction lastAction,
        int segmentsSinceLastAction,
        BatteryConfig config)
    {
        if (config.ActionReversalCooldownSegments <= 0) return proposed;
        if (proposed is EnergySegmentAction.None || lastAction is EnergySegmentAction.None) return proposed;
        if (proposed == lastAction) return proposed; // continuing a run, not reversing
        if (segmentsSinceLastAction >= config.ActionReversalCooldownSegments) return proposed;
        // Floor defence outranks the cooldown: never refuse to charge.
        if (proposed is EnergySegmentAction.Buy && proposedReason is EnergySegmentActionReason.Usage) return proposed;
        return EnergySegmentAction.None;
    }

    /// <summary>
    /// The pessimism weight to price one arbitrage leg with: the MORE pessimistic of the flat arbitrage
    /// weight and the runway risk weight the boundary solver would apply to the same segment
    /// (<see cref="EnergySegmentExtensions.GetRiskWeight"/>).
    ///
    /// Arbitrage is DISCRETIONARY where the boundary solver is MANDATORY, so it must never be the more
    /// optimistic of the two about the same segment. Taking the max does two things:
    ///  - Where the solver is more pessimistic (short runway, where it marks a future buy up past the
    ///    advanced High so it charges early), arbitrage inherits that markup. Otherwise arbitrage books a
    ///    cheap refill at a segment the solver has already decided it will not wait for — it sells now on
    ///    the promise of a price the rest of the planner refuses to plan around.
    ///  - Where the solver is OPTIMISTIC (deep runway: GetRiskWeight goes negative, down to
    ///    -OptimismMaxWeight), the flat arbitrage weight wins, so a refill is never priced BELOW its
    ///    predicted value. Deferring to the runway weight unconditionally would make arbitrage more
    ///    aggressive exactly when the battery is full — the opposite of the intent.
    /// Locked (materialised) legs are unaffected: WeightedPrice returns them raw whatever the weight.
    /// </summary>
    private static decimal LegWeight(EnergySegment segment, decimal arbitrageWeight, BatteryConfig config, decimal hourlyUsage)
    {
        var runway = EnergySegmentExtensions.GetHoursToEmpty(segment.EstimatedBatteryChargeKwh, config.MinCapacity, hourlyUsage);
        return Math.Max(arbitrageWeight, EnergySegmentExtensions.GetRiskWeight(runway, config));
    }

    // A leg is actionable for arbitrage only when WeightedPrice yields a real price rather than the
    // un-actionable sentinel (decimal.Max/MinValue). That sentinel is returned for an ESTIMATE with no
    // advanced (ML) band — a forecast past Amber's ~24h advanced horizon. ApplyArbitrage does arithmetic
    // on the leg price (buyCost / RoundTripEfficiency), so feeding it a sentinel overflows the decimal
    // range; these mirror the sentinel conditions in WeightedPrice so such legs are dropped, not picked.
    // (Candidates already require BuyPricePerKw/SellPricePerKw != null, so a locked leg is always real.)
    private static bool HasActionableBuyPrice(EnergySegment segment) => !segment.IsBuyEstimate || segment.AdvancedBuyPrice is not null;
    private static bool HasActionableSellPrice(EnergySegment segment) => !segment.IsSellEstimate || segment.AdvancedSellPrice is not null;

    private static bool FeasiblePair(int buyIndex, int sellIndex, List<EnergySegment> energySegments, BatteryConfig config, decimal tol)
    {
        if (buyIndex < sellIndex)
        {
            // Buy before sell: hold the extra kWh; check we don't charge to within one step of MaxCapacity
            // (the same one-step buffer OptimiseSegments reserves, so arbitrage can't park on the sell trigger).
            // The hold-window levels rise by the buy leg's applied delta, which subsumes the buy segment's
            // natural flow, so predict the held level the same way.
            for (var i = buyIndex; i < sellIndex; i++)
            {
                if (energySegments[i].EstimatedBatteryChargeKwh + config.SegmentChargeAmountKwh - energySegments[buyIndex].NaturalChargeDeltaKwh > config.MaxCapacity - config.SegmentChargeAmountKwh + tol)
                    return false;
            }
        }
        else
        {
            // Sell before buy: discharge early then refill; check we don't discharge to within one step of
            // MinCapacity (same one-step buffer, so arbitrage can't park on the buy trigger). The hold-window
            // levels fall by the sell leg's applied delta, which subsumes the sell segment's natural flow, so
            // predict the held level the same way.
            //
            // On top of the fixed one-step buffer, reserve a fraction of the drain PROJECTED SO FAR in the
            // hold window. The one-step buffer is a structural guard (it stops arbitrage parking the
            // projection exactly on the buy trigger); it is not, and was never sized as, a forecast-error
            // margin. But this branch's whole premise is "the battery survives on its own until the refill",
            // and what threatens that is error in the projected household drain — a quantity that grows with
            // how long the pair holds, while the fixed buffer does not. A pair whose window carries 15 kWh of
            // projected drain was being judged on the same 0.83 kWh margin as one carrying 0.3 kWh.
            //
            // The reserve accumulates from the sell rather than being sized on the whole window, because the
            // error that can have built up by segment i is the error in the drain projected between the sell
            // and i. So a short hold reserves almost nothing (little can go wrong) and a long one reserves in
            // proportion to what has to flow through it. ArbitrageHoldDrainReserveFraction is the tolerance
            // stated explicitly: 0.5 means "this pair must still hold if the drain runs 50% over estimate".
            // Left at 0 (unset) the behaviour is exactly the old fixed buffer.
            var projectedDrainSinceSell = 0m;
            for (var i = sellIndex; i < buyIndex; i++)
            {
                projectedDrainSinceSell += energySegments[i].UsageKwh;
                var drainErrorReserve = config.ArbitrageHoldDrainReserveFraction * projectedDrainSinceSell;
                if (energySegments[i].EstimatedBatteryChargeKwh - config.SegmentDischargeAmountKwh - energySegments[sellIndex].NaturalChargeDeltaKwh < config.MinCapacity + config.SegmentDischargeAmountKwh + drainErrorReserve - tol)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Finds the first point at which the projected charge is parked against a capacity limit and
    /// still moving the wrong way: at/below MinCapacity and still falling (buy needed), or
    /// at/above MaxCapacity and still rising (sell needed). Returns no boundary when within range.
    /// </summary>
    public static BoundaryResult CalculateBoundaryResult(List<EnergySegment> energySegments, BatteryConfig config)
    {
        for (int i = 1; i < energySegments.Count; i++)
        {
            var curSegment = energySegments[i - 1];
            var nextSegment = energySegments[i];
            if (curSegment.EstimatedBatteryChargeKwh <= config.MinCapacity &&
                nextSegment.EstimatedBatteryChargeKwh < curSegment.EstimatedBatteryChargeKwh)
            {
                return new BoundaryResult
                {
                    IsOutOfBounds = true,
                    IsMax = false,
                    IndexOfBoundaryCrossing = i
                };
            }
            if (config.MaxCapacity <= curSegment.EstimatedBatteryChargeKwh &&
                curSegment.EstimatedBatteryChargeKwh < nextSegment.EstimatedBatteryChargeKwh)
            {
                return new BoundaryResult
                {
                    IsOutOfBounds = true,
                    IsMax = true,
                    IndexOfBoundaryCrossing = i
                };
            }
        }
        return new BoundaryResult
        {
            IsOutOfBounds = false,
            IsMax = null,
            IndexOfBoundaryCrossing = null
        };
    }

    /// <summary>
    /// The UTC start of the first segment whose projected charge drops below MinCapacity, or
    /// <see cref="DateTime.MaxValue"/> if it never does.
    /// </summary>
    public static DateTime GetBatteryUntil(List<EnergySegment> energySegments, BatteryConfig config)
    {
        return energySegments.FirstOrDefault(segment => segment.EstimatedBatteryChargeKwh < config.MinCapacity)?.StartUtc ?? DateTime.MaxValue;
    }

    /// <summary>
    /// Walks back from the boundary crossing to the last segment where an action would already push
    /// the battery against the opposite limit, bounding the window in which a Buy/Sell may be placed.
    ///
    /// The bound is the one-step BAND (MinCapacity + step / MaxCapacity - step), not the raw limit. A buy
    /// added here is held forward to the crossing, so if any run-up segment is already within a step of
    /// Max, charging before it would hold the battery against the ceiling — over successive replans that
    /// walks it up to Max and forces a low-price sell. Bounding at the band defers the buy past such a
    /// segment (toward the deficit, where the battery has drained and has room), so "now" isn't grid-charged
    /// while near full. Symmetric for a sell near the floor. Uses the same natural-flow subsumption the
    /// apply step uses, so the predicted post-action level matches where the action actually leaves it.
    /// </summary>
    public static int GetPreviousBoundaryCrossingIndex(List<EnergySegment> energySegments, BoundaryResult boundaryResult, BatteryConfig config)
    {
        if (boundaryResult.IndexOfBoundaryCrossing is null || boundaryResult.IsMax is null) return 0;
        for (var i = boundaryResult.IndexOfBoundaryCrossing.Value; i >= 0; i--)
        {
            var curSegment = energySegments[i];
            if (boundaryResult.IsMax.Value
                    ? (curSegment.EstimatedBatteryChargeKwh - config.SegmentDischargeAmountKwh - curSegment.NaturalChargeDeltaKwh) <= config.MinCapacity + config.SegmentDischargeAmountKwh
                    : config.MaxCapacity - config.SegmentChargeAmountKwh <= (curSegment.EstimatedBatteryChargeKwh + config.SegmentChargeAmountKwh - curSegment.NaturalChargeDeltaKwh))
            {
                return i;
            }
        }
        return 0;
    }
}
