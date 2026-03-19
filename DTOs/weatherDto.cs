namespace semantic_kernel.Dtos;

public class Weather{
    public double temp {get; set;}
    public double temp_max {get; set;}
    public double temp_min {get; set;}
}

public class WeatherApiResponse
{
    public string? name { get; set; }
    public Weather? main { get; set; }
}
