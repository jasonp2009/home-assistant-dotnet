namespace src.apps.HassModel.Battery.Models;

public class BoundaryResult
{
    public bool IsOutOfBounds { get; set; }
    public bool? IsMax { get; set; }
    public int? IndexOfBoundaryCrossing { get; set; }
}