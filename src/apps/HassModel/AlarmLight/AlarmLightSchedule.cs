namespace src.apps.HassModel.AlarmLight;

public class AlarmLightSchedule
{
    public bool IsExecuting { get; set; }
    public DateTime AlarmTime { get; set; }
    public AlarmLightGroup Group { get; set; }
}