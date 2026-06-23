using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using src.apps.HassModel.Battery.Clients.ForecastSolarClient.Models;

namespace src.apps.HassModel.Battery.Clients.ForecastSolarClient;

public class ForecastSolarClient
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("https://api.forecast.solar/"),
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly ILogger<ForecastSolarClient> _logger;

    private readonly ForecastSolarClientSettings _settings;
    private Dictionary<DateTime, int>? _cachedForecast;

    public ForecastSolarClient(IOptions<ForecastSolarClientSettings> settings, ILogger<ForecastSolarClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }


    /// <summary>
    /// Fetches the watt-hours/period forecast. When <paramref name="actualProductionKwh"/> is supplied,
    /// today's measured cumulative production (absolute kWh) is passed via Forecast.Solar's <c>actual</c>
    /// query parameter, which recalibrates the forecast to match real output for the current day only
    /// (it does not affect later days). See https://doc.forecast.solar/actual.
    /// </summary>
    public async Task<Dictionary<DateTime, int>?> GetForecastAsync(decimal? actualProductionKwh = null)
    {
        try
        {
            var url =
                $"/{_settings.ApiKey}/estimate/watthours/period/{_settings.Latitude}/{_settings.Longitude}/{_settings.Declination}/{_settings.Azimuth}/{_settings.Kilowatts}?time=utc";
            if (actualProductionKwh is >= 0)
                url += $"&actual={actualProductionKwh.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
            var response = await _httpClient.GetFromJsonAsync<ForecastResult>(url);
            _cachedForecast = response?.Result.ToDictionary(v => DateTime.Parse(v.Key).ToUniversalTime(), v => v.Value);
            return _cachedForecast;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get solar forecast: {Message}", ex.Message);
            return _cachedForecast;
        }
    }
}