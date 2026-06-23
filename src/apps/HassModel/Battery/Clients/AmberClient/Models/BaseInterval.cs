using System.Text.Json.Serialization;
using src.apps.HassModel.Battery.Clients.AmberClient.Enums;

namespace src.apps.HassModel.Battery.Clients.AmberClient.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ActualInterval), typeDiscriminator: nameof(ActualInterval))]
[JsonDerivedType(typeof(CurrentInterval), typeDiscriminator: nameof(CurrentInterval))]
[JsonDerivedType(typeof(ForecastInterval), typeDiscriminator: nameof(ForecastInterval))]
[JsonDerivedType(typeof(Usage), typeDiscriminator: nameof(Usage))]
public class BaseInterval
{
    public IntervalDuration Duration { get; set; }
    public decimal SpotPerKwh { get; set; }
    public decimal PerKwh { get; set; }
    public DateTime Date { get; set; }
    public DateTime NemTime { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public decimal Renewables { get; set; }
    public ChannelType ChannelType { get; set; }
    public TariffInformation TariffInformation { get; set; }
    public SpikeStatus SpikeStatus { get; set; }
    public PriceDescriptor Descriptor { get; set; }
}