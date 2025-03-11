using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using src.apps.HassModel.AC.MitsubishiClient.Models;

namespace src.apps.HassModel.AC.MitsubishiClient;

public class MitsubishiClient : IMitsubishiClient
{
    private const string LoginRoute = "login.aspx";
    private const string UnitCommandRoute = "unitcommand.aspx";

    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("https://api.melview.net/api/")
    };

    private readonly ILogger<MitsubishiClient> _logger;

    private readonly IOptions<MitsubishiClientSettings> _settings;

    public MitsubishiClient(IOptions<MitsubishiClientSettings> settings, ILogger<MitsubishiClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public AcState State { get; set; }

    public async Task ToggleZone(int zoneId, bool? isOn = null, CancellationToken cancellationToken = default)
    {
        var zone = State.Zones.Single(zone => zone.ZoneId == zoneId);
        isOn ??= !zone.IsOn;
        if (zone.IsOn == isOn) return;

        _logger.LogInformation("Toggling zone {ZoneId} to {IsOn}", zoneId, isOn);

        await SendUnitCommand($"Z{zoneId}{Convert.ToInt32(isOn)}", cancellationToken);
    }

    public async Task SetTemperature(decimal temperature, CancellationToken cancellationToken = default)
    {
        var intTemp =
            Convert.ToInt32(State.SetMode == AcMode.Heat ? Math.Ceiling(temperature) : Math.Floor(temperature));
        if (State.SetTemp == intTemp) return;

        _logger.LogInformation("Setting temperature {Temperature}", temperature);

        await SendUnitCommand($"TS{intTemp}", cancellationToken);
    }

    public async Task SetMode(AcMode mode, CancellationToken cancellationToken = default)
    {
        if ((mode == AcMode.Auto && State.AutoMode) || mode == State.SetMode) return;

        _logger.LogInformation("Setting mode {Mode}", mode);

        await SendUnitCommand($"MD{Convert.ToInt32(mode)}", cancellationToken);
    }

    public async Task SetFanMode(AcFanMode fanMode, CancellationToken cancellationToken = default)
    {
        if (fanMode == State.SetFan) return;

        _logger.LogInformation("Setting fan mode {FanMode}", fanMode);

        await SendUnitCommand($"FS{Convert.ToInt32(fanMode)}", cancellationToken);
    }

    public async Task ToggleAc(bool? isOn = null, CancellationToken cancellationToken = default)
    {
        isOn ??= State.Power;
        if (State.Power == isOn) return;

        _logger.LogInformation("Toggling AC {IsOn}", isOn);

        await SendUnitCommand($"PW{Convert.ToInt32(isOn)}", cancellationToken);
    }


    public async Task Login(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(LoginRoute, new
        {
            User = _settings.Value.Username,
            Pass = _settings.Value.Password
        }, cancellationToken);
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            throw new InvalidCredentialException("Unable to login");
        _httpClient.DefaultRequestHeaders.Add("cookie", cookies);

        await UpdateState(cancellationToken);
    }

    public Task UpdateState(CancellationToken cancellationToken = default)
    {
        return SendUnitCommand(null, cancellationToken);
    }

    private async Task SendUnitCommand(string? commands = null, CancellationToken cancellationToken = default)
    {
        var responseMessage = await _httpClient.PostAsJsonAsync(UnitCommandRoute, new
        {
            UnitId = 0,
            V = 4,
            Commands = commands
        }, cancellationToken);
        await UpdateStateFromResponse(responseMessage, cancellationToken);
    }

    private async Task UpdateStateFromResponse(HttpResponseMessage responseMessage,
        CancellationToken cancellationToken = default)
    {
        var previousState = State;
        State = await responseMessage.Content.ReadFromJsonAsync<AcState>(cancellationToken) ?? State;
        if (State?.RoomTemp != previousState?.RoomTemp)
            _logger.LogDebug("AC measured room temp updated to {RoomTemp}", State?.RoomTemp);
    }
}