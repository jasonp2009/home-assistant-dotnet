using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using src.apps.HassModel.Battery.Clients.AmberClient.Models;

namespace src.apps.HassModel.Battery.Clients.AmberClient;

public class AmberClient
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("https://api.amber.com.au/")
    };

    private readonly ILogger<AmberClient> _logger;

    private readonly AmberClientSettings _settings;

    public AmberClient(IOptions<AmberClientSettings> settings, ILogger<AmberClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
    }

    public async Task<List<BaseInterval>?> GetCurrentPriceAsync(int forecastCount = 2048)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<List<BaseInterval>>(
                $"/v1/sites/{_settings.SiteId}/prices/current?next={forecastCount}&resolution=5");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to get amber prices: {Message} {@Exception}", ex.Message, ex);
            return null;
        }
    }
}