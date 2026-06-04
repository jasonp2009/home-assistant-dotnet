using System.Text.Json.Serialization;
using src.apps.HassModel.Battery.AmberClient.Enums;

namespace src.apps.HassModel.Battery.AmberClient.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ActualInterval), typeDiscriminator: nameof(ActualInterval))]
[JsonDerivedType(typeof(CurrentInterval), typeDiscriminator: nameof(CurrentInterval))]
[JsonDerivedType(typeof(ForecastInterval), typeDiscriminator: nameof(ForecastInterval))]
[JsonDerivedType(typeof(Usage), typeDiscriminator: nameof(Usage))]
public class BaseInterval
{
    public IntervalDuration Duration { get; set; }
    public double SpotPerKwh { get; set; }
    public double PerKwh { get; set; }
    public DateTimeOffset Date { get; set; }
    public DateTimeOffset NemTime { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public double Renewables { get; set; }
    public ChannelType ChannelType { get; set; }
    public TariffInformation TariffInformation { get; set; }
    public SpikeStatus SpikeStatus { get; set; }
    public PriceDescriptor Descriptor { get; set; }
}