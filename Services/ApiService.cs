namespace semantic_kernel.Services;

using System.Net.Http.Json;
using semantic_kernel.Dtos;
public class ApiService
{
    private readonly IHttpClientFactory _factory;

    public ApiService(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public async Task<MainWeather> SearchWeather(string ideasAgentOutput)
    {
        var client = _factory.CreateClient();

        string[] splitString = ideasAgentOutput.Split(" ");
        string lat = splitString[2];
        string lon = splitString[3];

        var response = await client.GetFromJsonAsync<MainWeather>(
            $"https://api.openweathermap.org/data/2.5/weather?lat={Convert.ToDouble(lat)}&lon={Convert.ToDouble(lon)}&appid={"API_KEY"}"
        );
        return response;
    }

}