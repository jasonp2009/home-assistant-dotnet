using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using src.apps.HassModel.AC;

namespace src;

public static class DependencyInjection
{
    public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMitsubishiClient(configuration);
        return services;
    }
}