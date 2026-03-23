using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using DotNetEnv;
using semantic_kernel.Dtos;
using Microsoft.SemanticKernel.Agents.Orchestration.Sequential;
using Microsoft.SemanticKernel.Agents.Runtime.Core;
using Microsoft.SemanticKernel.Services;
using semantic_kernel.Services;
using System.Globalization;
using System.Text.Json;

var builder = Kernel.CreateBuilder()
    .AddOllamaChatCompletion(
        modelId: "llama3.1:latest",
        endpoint: new Uri("http://localhost:11434")
    );

Env.Load();

var kernel = builder.Build();

var serviceCollection = new ServiceCollection();
serviceCollection.AddHttpClient();
serviceCollection.AddSingleton<ApiService>();
using var serviceProvider = serviceCollection.BuildServiceProvider();
var apiService = serviceProvider.GetRequiredService<ApiService>();

var ideasAgent = new ChatCompletionAgent
{
    Name = "Ideas",
    Instructions =
        "You are a Madeira island tourist guide." +
        "Your job is to give 1 place ideias to visit or enjoy in Madeira island based on the location in the user input.\n" +
        "Return ONLY a JSON object with these fields: name, location, lat, lon. Use decimal degrees with dot (.)\n" +
        "Example: {\"name\":\"Pico do Areeiro\",\"location\":\"Madeira\",\"lat\":32.735,\"lon\":-16.928}",
    Kernel = kernel
};

var weatherAgent = new ChatCompletionAgent{
    Name = "Weather",
    Instructions =
        "You are a weather specialist agent." +
        "You will receive JSON with place info and temperatures (Celsius) already fetched from a weather API.\n" +
        "Your job is to summarize the weather in Portuguese, include temp/temp_min/temp_max, and classify it as 'muito frio', 'frio', 'agradável', 'quente' or 'muito quente'.\n" +
        "Use ONLY the numbers provided in the JSON.",
    Kernel = kernel
};

var interpreterAgent = new ChatCompletionAgent
{
    Name = "Interpreter",
    Instructions =
        "You are an text interpreter specialist." +
        "Your job is to recommend the place to visit based on the user request and the weather summary.\n" +
        "Output in Portuguese: place name, location, coordinates, current temperature, and a short recommendation.",
    Kernel = kernel
};

string ideasOutput = "";
string weatherOutput = "";
string interpreterOutput = "";

static string ExtractFirstJsonObject(string text)
{
    int start = text.IndexOf('{');
    int end = text.LastIndexOf('}');
    if (start < 0 || end < 0 || end <= start) return text;
    return text.Substring(start, end - start + 1);
}

static bool TryParsePlace(string text, out PlaceRecommendation place)
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    string json = ExtractFirstJsonObject(text);
    try
    {
        var parsed = JsonSerializer.Deserialize<PlaceRecommendation>(json, options);
        if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.name) && !string.IsNullOrWhiteSpace(parsed.location))
        {
            place = parsed;
            return true;
        }
    }
    catch { }

    string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length >= 4
        && double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
        && double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
    {
        place = new PlaceRecommendation
        {
            name = parts[0],
            location = string.Join(' ', parts[1..^2]),
            lat = lat,
            lon = lon
        };
        return true;
    }

    place = new PlaceRecommendation { name = "Unknown", location = "Madeira", lat = 0, lon = 0 };
    return false;
}

do
{
    Console.Write("User > ");
    string? userInput = System.Console.ReadLine();
    if (userInput is null || userInput.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    ideasOutput = "";
    weatherOutput = "";
    interpreterOutput = "";
    
    await foreach (var response in ideasAgent.InvokeAsync(userInput))
    {
        ideasOutput += response.Message.Content;
    }

    if (!TryParsePlace(ideasOutput, out var place))
    {
        Console.WriteLine("Assistant > Não consegui interpretar as coordenadas. Tenta novamente.");
        continue;
    }

    WeatherApiResponse? weatherApiResponse;
    try
    {
        weatherApiResponse = await apiService.SearchWeather(place.lat, place.lon);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Assistant > Erro ao chamar a API do tempo: " + ex.Message);
        continue;
    }

    var payload = new
    {
        place,
        weather = new
        {
            temp = weatherApiResponse?.main?.temp,
            temp_min = weatherApiResponse?.main?.temp_min,
            temp_max = weatherApiResponse?.main?.temp_max,
            units = "C"
        }
    };

    string weatherInput = JsonSerializer.Serialize(payload);

    await foreach (var response in weatherAgent.InvokeAsync(weatherInput))
    {
        weatherOutput += response.Message.Content;
    }

    string interpreterInput =
        "Pedido do utilizador: " + userInput + "\n"
        + "Sugestão de lugar: " + place.name + " (" + place.location + $") [{place.lat.ToString(CultureInfo.InvariantCulture)},{place.lon.ToString(CultureInfo.InvariantCulture)}]\n"
        + "Resumo do tempo: " + weatherOutput;

    await foreach (var response in interpreterAgent.InvokeAsync(interpreterInput))
    {
        interpreterOutput += response.Message.Content;
    }

    Console.WriteLine("Assistant > " + interpreterOutput);
} while (true);

public sealed class PlaceRecommendation
{
    public string? name { get; set; }
    public string? location { get; set; }
    public double lat { get; set; }
    public double lon { get; set; }
}
