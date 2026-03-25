using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using DotNetEnv;
using semantic_kernel.Dtos;
using semantic_kernel.Services;
using Microsoft.EntityFrameworkCore;
using Location = semantic_kernel.Models.Location;
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
var locationsDbPath = Path.Combine(AppContext.BaseDirectory, "locations.db");
serviceCollection.AddDbContext<LocationsDbContext>(options =>
    options.UseSqlite($"Data Source={locationsDbPath}"));
using var serviceProvider = serviceCollection.BuildServiceProvider();
var apiService = serviceProvider.GetRequiredService<ApiService>();

using (var scope = serviceProvider.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LocationsDbContext>();
    db.Database.EnsureCreated();
    await EnsureLocationsSchemaAsync(db);
    await EnsureLocationsSeededAsync(db);
}

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
        "You will receive JSON with place info and the current temperature (Celsius) already fetched from a weather API.\n" +
        "Your job is to output ONLY the current temperature in Portuguese (e.g., \"18°C\").\n" +
        "Use ONLY the number provided in the JSON.",
    Kernel = kernel
};

var interpreterAgent = new ChatCompletionAgent
{
    Name = "Interpreter",
    Instructions =
        "You are an text interpreter specialist." +
        "Your job is to output a formatted table (plain text) with all locations and their current temperature.\n" +
        "Do not call external APIs. Use ONLY the data provided.\n" +
        "Columns: Location | Name | Latitude | Longitude | Temperature (C) | LastUpdated (ISO 8601).",
    Kernel = kernel
};

string ideasOutput = "";
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

static async Task EnsureLocationsSchemaAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
{
    try
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE Locations ADD COLUMN Temperature REAL NOT NULL DEFAULT 0",
            cancellationToken);
    }
    catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
    {
    }
}

static async Task EnsureLocationsSeededAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
{
    if (await db.Locations.AnyAsync(cancellationToken))
    {
        return;
    }

    db.Locations.AddRange(
        new Location { Name = "Funchal", Latitude = 32.6669, Longitude = -16.9241, Type = "city", Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue },
        new Location { Name = "Pico do Areeiro", Latitude = 32.7356, Longitude = -16.9289, Type = "nature", Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue },
        new Location { Name = "Porto Moniz", Latitude = 32.8668, Longitude = -17.1662, Type = "nature", Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue },
        new Location { Name = "Santana", Latitude = 32.8007, Longitude = -16.8801, Type = "rural", Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue },
        new Location { Name = "Ponta de São Lourenço", Latitude = 32.7403, Longitude = -16.7014, Type = "nature", Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue }
    );

    await db.SaveChangesAsync(cancellationToken);
}

static async Task RefreshAllTemperaturesAsync(LocationsDbContext db, ApiService apiService, CancellationToken cancellationToken = default)
{
    var locations = await db.Locations.ToListAsync(cancellationToken);
    if (locations.Count == 0)
    {
        return;
    }

    foreach (var location in locations)
    {
        try
        {
            var weather = await apiService.SearchWeather(location.Latitude, location.Longitude, cancellationToken);
            var temp = weather?.main?.temp;
            if (temp is not null)
            {
                location.Temperature = temp.Value;
                location.LastUpdated = DateTime.UtcNow;
            }
        }
        catch
        {
        }
    }

    await db.SaveChangesAsync(cancellationToken);
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
    interpreterOutput = "";

    using var scope = serviceProvider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LocationsDbContext>();

    await EnsureLocationsSeededAsync(db);
    await RefreshAllTemperaturesAsync(db, apiService);

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

    var temperature = weatherApiResponse?.main?.temp;
    if (temperature is null)
    {
        Console.WriteLine("Assistant > Não consegui obter a temperatura atual. Tenta novamente.");
        continue;
    }

    var now = DateTime.UtcNow;
    var placeName = (place.name ?? "Unknown").Trim();

    var existingLocation = await db.Locations.FirstOrDefaultAsync(l => l.Name.ToLower() == placeName.ToLower());
    if (existingLocation is null)
    {
        db.Locations.Add(new Location
        {
            Name = placeName,
            Latitude = place.lat,
            Longitude = place.lon,
            Temperature = temperature.Value,
            LastUpdated = now,
            Type = "unknown",
            Weather = "N/A"
        });
    }
    else
    {
        existingLocation.Name = placeName;
        existingLocation.Latitude = place.lat;
        existingLocation.Longitude = place.lon;
        existingLocation.Temperature = temperature.Value;
        existingLocation.LastUpdated = now;
    }

    await db.SaveChangesAsync();

    var payload = new
    {
        place,
        weather = new
        {
            temp = temperature.Value,
            units = "C"
        }
    };

    string weatherInput = JsonSerializer.Serialize(payload);

    await foreach (var response in weatherAgent.InvokeAsync(weatherInput))
    {
        _ = response;
    }

    var locationsSnapshot = await db.Locations.AsNoTracking()
        .OrderBy(l => l.Name)
        .Select(l => new
        {
            Location = "Madeira",
            l.Name,
            l.Latitude,
            l.Longitude,
            Temperature = l.Temperature.ToString("0.#", CultureInfo.InvariantCulture) + " ºC",
            l.LastUpdated
        })
        .ToListAsync();

    string interpreterInput =
        "Dados atuais da base de dados (fonte única de verdade):\n"
        + JsonSerializer.Serialize(locationsSnapshot, new JsonSerializerOptions { WriteIndented = true });

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
