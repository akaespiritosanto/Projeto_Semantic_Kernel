namespace semantic_kernel.Models;

public class Coord
{
    public double lon { get; set; }
    public double lat { get; set; }
}

public class MainWeather
{
    public double temp { get; set; }
    public double temp_max { get; set; }
    public double temp_min { get; set; }
}

public class WeatherApiResponse
{
    public string? name { get; set; }
    public Coord? coord { get; set; }
    public MainWeather? main { get; set; }
}
