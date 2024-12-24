namespace src.apps.HassModel.AC.MitsubishiClient.Models;

public class AcZone
{
    public int ZoneId { get; set; }
    public string Name { get; set; }
    public AcZoneStatus Status { get; set; }

    public bool IsOn => Status != AcZoneStatus.Off;
}