using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging;
using src.apps.HassModel.Battery.Clients.HaHistoryClient;
using src.apps.HassModel.Battery.Models;

namespace src.apps.HassModel.Battery.Usage;

/// <summary>
/// Learns per-time-of-day household consumption and serves it back as a per-segment usage estimate.
/// State (the sample set and the live measurement-window anchor) lives in memory: on startup it is
/// backfilled from Home Assistant's history API, then extended each run from live counter reads. A
/// process restart simply re-pulls from HA (which kept recording), so restarts self-heal.
///
/// All the maths is in <see cref="UsageMath"/> (pure, unit-tested); this class is the Home Assistant /
/// history-IO orchestration around it. Owned (and kept alive across runs) by <see cref="BatteryControl"/>.
/// </summary>
public class UsageTracker
{
    private readonly BatteryConfig _config;
    private readonly HaHistoryClient _historyClient;
    private readonly ILogger<UsageTracker> _logger;

    private readonly List<UsageSample> _samples = [];
    private CounterReading? _anchor;
    private bool _backfilled;

    public UsageTracker(BatteryConfig config, HaHistoryClient historyClient, ILogger<UsageTracker> logger)
    {
        _config = config;
        _historyClient = historyClient;
        _logger = logger;
    }

    /// <summary>
    /// Backfills the in-memory sample set from <c>UsageBackfillDays</c> of HA history on first use.
    /// Retries on a later run if the history call fails (returns null). Merges into any samples already
    /// gathered live and never wipes them.
    /// </summary>
    public async Task EnsureBackfilledAsync()
    {
        if (_backfilled) return;
        try
        {
            var now = DateTime.UtcNow;
            var start = now - TimeSpan.FromDays(_config.UsageBackfillDays);
            var history = await _historyClient.GetHistoryAsync(EntityIds, start);
            if (history is null)
            {
                _logger.LogWarning("Usage backfill skipped: no history returned; will retry next run");
                return;
            }

            var readings = BuildBoundaryReadings(history, start, now);
            var samples = UsageMath.BuildSamplesFromReadings(readings, _config);

            var existing = _samples.Select(s => s.SegmentStartUtc).ToHashSet();
            _samples.AddRange(samples.Where(s => !existing.Contains(s.SegmentStartUtc)));
            if (readings.Count > 0) _anchor = readings[^1];
            _backfilled = true;

            _logger.LogInformation("Usage backfill: {Samples} samples from {Readings} boundary readings over {Days}d",
                samples.Count, readings.Count, _config.UsageBackfillDays);
        }
        catch (Exception ex)
        {
            _logger.LogError("Usage backfill failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Reads the counters for the current segment and extends the measurement window. Closes and emits
    /// (spread across its 5-minute buckets) when the solar counter advances or the window hits the cap;
    /// otherwise leaves the window open. No-ops on a bad read so a glitch can't corrupt the set.
    /// </summary>
    public void Record()
    {
        var cur = ReadCounters();
        if (cur is null) return;
        if (_anchor is null) { _anchor = cur; return; }

        var k = (int)Math.Round((cur.TimestampUtc - _anchor.TimestampUtc) / _config.SegmentSize, MidpointRounding.AwayFromZero);
        if (k < 1) return; // same segment / clock skew — keep the window open
        var solarAdvanced = cur.SolarKwh > _anchor.SolarKwh;
        if (!solarAdvanced && k < _config.UsageMaxWindowSegments) return; // window stays open

        _samples.AddRange(UsageMath.SpreadWindow(_anchor, cur, _config));
        _anchor = cur;
        PruneOld(cur.TimestampUtc);
    }

    /// <summary>
    /// Snapshots the current samples and returns a per-segment usage estimator (kWh per segment) for
    /// use by the planner. <paramref name="fallbackKwh"/> (the flat average, already multiplier-adjusted)
    /// is returned for any time-of-day bucket with no data.
    /// </summary>
    public Func<DateTime, decimal> BuildEstimator(decimal fallbackKwh)
    {
        var nowUtc = DateTime.UtcNow;
        var snapshot = _samples.ToList();
        return target => UsageMath.EstimateSegmentUsage(snapshot, target, nowUtc, _config, fallbackKwh);
    }

    private string[] EntityIds =>
    [
        _config.GridEnergyInTotalEntity.EntityId,
        _config.GridEnergyOutTotalEntity.EntityId,
        _config.SolarLifetimeOutputEntity.EntityId,
        _config.BatteryEnergyChargingEntity.EntityId,
        _config.BatteryEnergyDischargingEntity.EntityId
    ];

    private CounterReading? ReadCounters()
    {
        try
        {
            return new CounterReading(
                UsageMath.SegmentStart(DateTime.UtcNow, _config.SegmentSize),
                ParseState(_config.GridEnergyInTotalEntity),
                ParseState(_config.GridEnergyOutTotalEntity),
                ParseState(_config.SolarLifetimeOutputEntity),
                ParseState(_config.BatteryEnergyChargingEntity),
                ParseState(_config.BatteryEnergyDischargingEntity));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read usage counters: {Message}", ex.Message);
            return null;
        }
    }

    private static decimal ParseState(SensorEntity entity)
    {
        if (!decimal.TryParse(entity.State, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"{entity.EntityId} state '{entity.State}' is not numeric");
        return value;
    }

    /// <summary>
    /// Reconstructs a dense series of <see cref="CounterReading"/>s at every 5-minute boundary in the
    /// window by step-interpolating each counter (its last value at or before the boundary). Boundaries
    /// before a counter has any history are skipped. Returns empty if any counter is missing entirely.
    /// </summary>
    private List<CounterReading> BuildBoundaryReadings(
        Dictionary<string, List<(DateTime TimeUtc, decimal Value)>> history, DateTime startUtc, DateTime endUtc)
    {
        var gridIn = Series(history, _config.GridEnergyInTotalEntity.EntityId);
        var gridOut = Series(history, _config.GridEnergyOutTotalEntity.EntityId);
        var solar = Series(history, _config.SolarLifetimeOutputEntity.EntityId);
        var charge = Series(history, _config.BatteryEnergyChargingEntity.EntityId);
        var discharge = Series(history, _config.BatteryEnergyDischargingEntity.EntityId);
        if (gridIn is null || gridOut is null || solar is null || charge is null || discharge is null)
        {
            _logger.LogWarning("Usage backfill: one or more counters missing from history");
            return [];
        }

        var segment = _config.SegmentSize;
        var readings = new List<CounterReading>();
        int pGridIn = 0, pGridOut = 0, pSolar = 0, pCharge = 0, pDischarge = 0;
        for (var b = UsageMath.SegmentStart(startUtc, segment) + segment; b <= endUtc; b += segment)
        {
            var vGridIn = ValueAt(gridIn, b, ref pGridIn);
            var vGridOut = ValueAt(gridOut, b, ref pGridOut);
            var vSolar = ValueAt(solar, b, ref pSolar);
            var vCharge = ValueAt(charge, b, ref pCharge);
            var vDischarge = ValueAt(discharge, b, ref pDischarge);
            if (vGridIn is null || vGridOut is null || vSolar is null || vCharge is null || vDischarge is null)
                continue; // no data yet for at least one counter at this boundary
            readings.Add(new CounterReading(b, vGridIn.Value, vGridOut.Value, vSolar.Value, vCharge.Value, vDischarge.Value));
        }
        return readings;
    }

    private static List<(DateTime TimeUtc, decimal Value)>? Series(
        Dictionary<string, List<(DateTime TimeUtc, decimal Value)>> history, string entityId)
        => history.TryGetValue(entityId, out var series) ? series.OrderBy(p => p.TimeUtc).ToList() : null;

    private static decimal? ValueAt(List<(DateTime TimeUtc, decimal Value)> series, DateTime at, ref int idx)
    {
        if (series.Count == 0) return null;
        while (idx + 1 < series.Count && series[idx + 1].TimeUtc <= at) idx++;
        return series[idx].TimeUtc <= at ? series[idx].Value : null;
    }

    private void PruneOld(DateTime nowUtc)
    {
        var maxDays = _config.UsageEstimateWindows.Select(w => w.Days).DefaultIfEmpty(0).Max();
        if (maxDays <= 0) return;
        var cutoff = nowUtc - TimeSpan.FromDays(maxDays + 1);
        _samples.RemoveAll(s => s.SegmentStartUtc < cutoff);
    }
}
