using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using src.apps.HassModel.AC.MitsubishiClient;

namespace src.apps.HassModel.AC;

public static class DependencyInjection
{
    public static void AddMitsubishiClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MitsubishiClientSettings>(configuration.GetSection(nameof(MitsubishiClientSettings)));
        services.AddHttpClient<MitsubishiClient.MitsubishiClient>();
        services.AddScoped<IMitsubishiClient, MitsubishiClient.MitsubishiClient>();
    }
}