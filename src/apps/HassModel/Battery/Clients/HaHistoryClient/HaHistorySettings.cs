namespace src.apps.HassModel.Battery.Clients.HaHistoryClient;

/// <summary>
/// Connection settings for the Home Assistant REST history API, bound from the existing
/// <c>HomeAssistant</c> section of <c>appsettings.json</c> (the same Host/Port/Ssl/Token NetDaemon uses).
/// </summary>
public class HaHistorySettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool Ssl { get; set; }
    public string Token { get; set; } = "";
}
