using System.Threading;
using System.Threading.Tasks;
using src.apps.HassModel.AC.MitsubishiClient.Models;

namespace src.apps.HassModel.AC.MitsubishiClient;

public interface IMitsubishiClient
{
    public AcState State { get; set; }
    public Task Login(CancellationToken cancellationToken = default);
    public Task ToggleZone(int zoneId, bool? isOn = null, CancellationToken cancellationToken = default);
    public Task SetTemperature(decimal temperature, CancellationToken cancellationToken = default);
    public Task SetMode(AcMode mode, CancellationToken cancellationToken = default);
    public Task SetFanMode(AcFanMode fanMode, CancellationToken cancellationToken = default);
    public Task ToggleAc(bool? isOn = null, CancellationToken cancellationToken = default);
    public Task UpdateState(CancellationToken cancellationToken = default);
}