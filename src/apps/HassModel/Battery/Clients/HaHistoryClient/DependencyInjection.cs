using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace src.apps.HassModel.Battery.Clients.HaHistoryClient;

public static class DependencyInjection
{
    public static void AddHaHistoryClient(this IServiceCollection services, IConfiguration configuration)
    {
        // HaHistoryClient uses NetDaemon's IHomeAssistantApiManager (already registered by the runtime),
        // so no connection settings are bound here.
        services.AddScoped<HaHistoryClient>();
    }
}
