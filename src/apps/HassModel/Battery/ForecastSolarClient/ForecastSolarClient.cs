using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using src.apps.HassModel.Battery.ForecastSolarClient.Models;

namespace src.apps.HassModel.Battery.ForecastSolarClient;

public class ForecastSolarClient
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("https://api.forecast.solar/")
    };

    private readonly ILogger<ForecastSolarClient> _logger;

    private readonly ForecastSolarClientSettings _settings;

    public ForecastSolarClient(IOptions<ForecastSolarClientSettings> settings, ILogger<ForecastSolarClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }


    public async Task<Dictionary<DateTime, int>?> GetForecastAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<ForecastResult>(
                $"/{_settings.ApiKey}/estimate/watthours/period/{_settings.Latitude}/{_settings.Longitude}/{_settings.Declination}/{_settings.Azimuth}/{_settings.Kilowatts}?time=utc");
            return response?.Result.ToDictionary(v => DateTime.Parse(v.Key).ToUniversalTime(), v => v.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get solar forecast: {Message} {@Exception}", ex.Message, ex);
            return null;
        }
    }
}