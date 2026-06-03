using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace src.apps.HassModel.Battery.ForecastSolarClient;

public static class DependencyInjection
{
    public static void AddForecastSolarClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForecastSolarClientSettings>(configuration.GetSection(nameof(ForecastSolarClientSettings)));
        services.AddHttpClient<ForecastSolarClient>();
        services.AddScoped<ForecastSolarClient>();
    }
}