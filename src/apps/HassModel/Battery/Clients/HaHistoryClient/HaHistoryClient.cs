using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetDaemon.Client;

namespace src.apps.HassModel.Battery.Clients.HaHistoryClient;

/// <summary>
/// Minimal client for Home Assistant's REST history endpoint
/// (<c>GET /api/history/period/{start}?...&amp;minimal_response</c>), used to backfill the usage
/// estimate on startup. Calls go through NetDaemon's <see cref="IHomeAssistantApiManager"/> so they
/// reuse the app's existing authenticated connection. A hand-rolled <c>HttpClient</c> against the
/// configured host/token works locally but is rejected with 403 inside the HA add-on (which talks to
/// core via the Supervisor proxy/token) — the API manager handles both environments.
/// </summary>
public class HaHistoryClient
{
    private readonly IHomeAssistantApiManager _apiManager;
    private readonly ILogger<HaHistoryClient> _logger;

    public HaHistoryClient(IHomeAssistantApiManager apiManager, ILogger<HaHistoryClient> logger)
    {
        _apiManager = apiManager;
        _logger = logger;
    }

    /// <summary>
    /// Pulls state history for the given entities from <paramref name="startUtc"/> to now. Returns a
    /// per-entity, time-ordered series of (UTC timestamp, numeric value); non-numeric states
    /// (unavailable/unknown) are skipped. Returns null on any failure so the caller can fall back and
    /// retry later.
    /// </summary>
    public async Task<Dictionary<string, List<(DateTime TimeUtc, decimal Value)>>?> GetHistoryAsync(
        IReadOnlyCollection<string> entityIds, DateTime startUtc)
    {
        try
        {
            var start = startUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";
            // end_time is required: HA defaults it to start + 1 day, which would truncate the backfill.
            var end = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";
            var filter = string.Join(",", entityIds);
            // Path is relative to HA's /api/ base; IHomeAssistantApiManager prepends the base URL + auth.
            var apiPath = $"history/period/{start}?filter_entity_id={filter}&minimal_response&end_time={Uri.EscapeDataString(end)}";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var root = await _apiManager.GetApiCallAsync<JsonElement>(apiPath, cts.Token);
            if (root is not { ValueKind: JsonValueKind.Array } entities)
            {
                _logger.LogWarning("HA history returned no array (kind {Kind})", root.ValueKind);
                return null;
            }

            var result = new Dictionary<string, List<(DateTime, decimal)>>();
            foreach (var entityArray in entities.EnumerateArray())
            {
                string? entityId = null;
                var series = new List<(DateTime, decimal)>();
                foreach (var point in entityArray.EnumerateArray())
                {
                    if (entityId is null && point.TryGetProperty("entity_id", out var idEl))
                        entityId = idEl.GetString();

                    if (!point.TryGetProperty("state", out var stateEl)) continue;
                    if (!decimal.TryParse(stateEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                        continue;

                    var timeEl = point.TryGetProperty("last_changed", out var lc) ? lc
                        : point.TryGetProperty("last_updated", out var lu) ? lu : default;
                    if (timeEl.ValueKind == JsonValueKind.Undefined || !timeEl.TryGetDateTimeOffset(out var dto))
                        continue;

                    series.Add((dto.UtcDateTime, value));
                }
                if (entityId is not null) result[entityId] = series;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get HA history: {Message}", ex.Message);
            return null;
        }
    }
}
