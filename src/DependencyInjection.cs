using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using src.apps.HassModel.AC;
using src.apps.HassModel.Battery.Clients.AmberClient;
using src.apps.HassModel.Battery.Clients.ForecastSolarClient;
using src.apps.HassModel.Battery.Clients.HaHistoryClient;

namespace src;

public static class DependencyInjection
{
    public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMitsubishiClient(configuration);
        services.AddForecastSolarClient(configuration);
        services.AddAmberClient(configuration);
        services.AddHaHistoryClient(configuration);
        return services;
    }
}