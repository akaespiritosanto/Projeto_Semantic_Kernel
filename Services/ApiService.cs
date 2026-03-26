namespace semantic_kernel.Services;

using System.Globalization;
using System.Net.Http.Json;
using semantic_kernel;
using semantic_kernel.Dtos;
public class ApiService
{
    private readonly HttpClient _httpClient;

    public ApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WeatherApiResponse?> SearchWeather(double lat, double lon, CancellationToken cancellationToken = default)
    {
        string? apiKey = Environment.GetEnvironmentVariable("API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            DebugUtil.Log("Missing environment variable API_KEY (OpenWeather).");
            throw new InvalidOperationException("Missing environment variable API_KEY (OpenWeather).");
        }

        string urlForLog =
            "https://api.openweathermap.org/data/2.5/weather"
            + $"?lat={lat.ToString(CultureInfo.InvariantCulture)}"
            + $"&lon={lon.ToString(CultureInfo.InvariantCulture)}"
            + "&appid=***"
            + "&units=metric"
            + "&lang=pt";
        DebugUtil.Log($"OpenWeather request: GET {urlForLog}");

        string url =
            "https://api.openweathermap.org/data/2.5/weather"
            + $"?lat={lat.ToString(CultureInfo.InvariantCulture)}"
            + $"&lon={lon.ToString(CultureInfo.InvariantCulture)}"
            + $"&appid={Uri.EscapeDataString(apiKey)}"
            + "&units=metric"
            + "&lang=pt";

        return await _httpClient.GetFromJsonAsync<WeatherApiResponse>(url, cancellationToken: cancellationToken);
    }

}
