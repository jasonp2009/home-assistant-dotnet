using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace src.apps.HassModel.Battery.Clients.AmberClient;

public static class DependencyInjection
{
    public static void AddAmberClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AmberClientSettings>(configuration.GetSection(nameof(AmberClientSettings)));
        services.AddScoped<AmberClient>();
    }
}