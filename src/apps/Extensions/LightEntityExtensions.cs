using HomeAssistantGenerated;

namespace src.apps.Extensions;

public static class LightEntityExtensions
{
    public static double GetTemperaturePercentage(this LightEntity light)
    {
        return (light.Attributes?.ColorTempKelvin - light.Attributes?.MinColorTempKelvin) * 100 /
            (light.Attributes?.MaxColorTempKelvin - light.Attributes?.MinColorTempKelvin) ?? 100;
    }

    public static double? PercentageToKelvin(this LightEntity light, double percentage)
    {
        var decimalKelvin = percentage.CleanPercentage() / 100;
        return (light.Attributes?.MaxColorTempKelvin - light.Attributes?.MinColorTempKelvin) * decimalKelvin +
               light.Attributes?.MinColorTempKelvin;
    }

    public static double CleanPercentage(this double percentage)
    {
        return Math.Min(Math.Abs(percentage), 100);
    }
}