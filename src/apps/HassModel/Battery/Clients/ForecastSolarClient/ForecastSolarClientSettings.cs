namespace src.apps.HassModel.Battery.Clients.ForecastSolarClient;

public class ForecastSolarClientSettings
{
    public string ApiKey { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal Declination { get; set; }
    public decimal Azimuth { get; set; }
    public decimal Kilowatts { get; set; }
}