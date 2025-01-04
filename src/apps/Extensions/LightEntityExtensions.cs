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
        var decimalKelvin = percentage / 100;
        return (light.Attributes?.MaxColorTempKelvin - light.Attributes?.MinColorTempKelvin) * decimalKelvin +
               light.Attributes?.MinColorTempKelvin;
    }
}