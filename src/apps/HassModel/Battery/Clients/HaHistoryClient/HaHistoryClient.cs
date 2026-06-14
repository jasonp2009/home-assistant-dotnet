using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace src.apps.HassModel.Battery.Clients.HaHistoryClient;

/// <summary>
/// Minimal client for Home Assistant's REST history endpoint
/// (<c>GET /api/history/period/{start}?filter_entity_id=...&amp;minimal_response</c>), used to backfill
/// the usage estimate on startup. HA is the source of truth for historical counter values.
/// </summary>
public class HaHistoryClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HaHistoryClient> _logger;

    public HaHistoryClient(IOptions<HaHistorySettings> settings, ILogger<HaHistoryClient> logger)
    {
        _logger = logger;
        var s = settings.Value;
        var scheme = s.Ssl ? "https" : "http";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{scheme}://{s.Host}:{s.Port}/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
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
            var filter = string.Join(",", entityIds);
            var url = $"api/history/period/{start}?filter_entity_id={filter}&minimal_response";

            using var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var result = new Dictionary<string, List<(DateTime, decimal)>>();
            foreach (var entityArray in doc.RootElement.EnumerateArray())
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
