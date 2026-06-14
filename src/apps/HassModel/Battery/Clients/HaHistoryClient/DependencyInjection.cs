using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace src.apps.HassModel.Battery.Clients.HaHistoryClient;

public static class DependencyInjection
{
    public static void AddHaHistoryClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HaHistorySettings>(configuration.GetSection("HomeAssistant"));
        services.AddScoped<HaHistoryClient>();
    }
}
