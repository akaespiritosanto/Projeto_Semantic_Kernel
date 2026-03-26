using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using DotNetEnv;
using semantic_kernel;
using semantic_kernel.Dtos;
using semantic_kernel.Services;
using Microsoft.EntityFrameworkCore;
using Location = semantic_kernel.Models.Location;
using System.Globalization;
using System.Text.Json;

// ====================================================================================================================
// Kernel / LLM setup
// ====================================================================================================================
var builder = Kernel.CreateBuilder()
    .AddOllamaChatCompletion(
        modelId: "llama3.1:latest",
        endpoint: new Uri("http://localhost:11434")
    );

// ====================================================================================================================
// Environment
// ====================================================================================================================
Env.Load();
DebugUtil.Log("Loaded environment (.env).");

var kernel = builder.Build();
DebugUtil.Log("Semantic Kernel built.");

// ====================================================================================================================
// HTTP + DB setup
// ====================================================================================================================
var locationsDbPath = Path.Combine(AppContext.BaseDirectory, "locations.db");
DebugUtil.Log($"SQLite DB path: {locationsDbPath}");
var dbOptions = new DbContextOptionsBuilder<LocationsDbContext>()
    .UseSqlite($"Data Source={locationsDbPath}")
    .Options;

using var httpClient = new HttpClient();
var apiService = new ApiService(httpClient);

LocationsDbContext CreateDb() => new(dbOptions);

await using (var db = CreateDb())
{
    db.Database.EnsureCreated();
    await EnsureLocationsSchemaAsync(db);
    await EnsureLocationsSeededAsync(db);
    await RefreshAllTemperaturesAsync(db, apiService);
    DebugUtil.Log($"DB ready. Locations count: {await db.Locations.CountAsync()}");
}

// ====================================================================================================================
// Agents
// ====================================================================================================================
var ideasAgent = new ChatCompletionAgent
{
    Name = "Ideas",
    Instructions = """
    You are a Madeira island tourist guide.
    Your job is to give 1 place idea to visit or enjoy in Madeira island based on the location in the user input.

    Return ONLY a JSON object with these fields: name, location, lat, lon.
    Use decimal degrees with dot (.)

    Example: {"name":"Pico do Areeiro","location":"Madeira","lat":32.735,"lon":-16.928}
    """,
    Kernel = kernel
};

var weatherAgent = new ChatCompletionAgent
{
    Name = "Weather",
    Instructions = """
    You are a weather specialist agent.
    You will receive JSON with a temperature (Celsius) already fetched from a weather API.

    Output ONLY the temperature in Portuguese in a short format like "18°C" (no extra text, no code fences).
    Use ONLY the number provided in the JSON.
    """,
    Kernel = kernel
};

var interpreterAgent = new ChatCompletionAgent
{
    Name = "Interpreter",
    Instructions = """
    You are a text interpreter specialist.
    You will receive JSON rows with locations and temperatures.

    Output ONLY ONE plain-text table with all rows (no extra text, no code fences, no repeated headers).
    Columns: Location | Name | Latitude | Longitude | Temperature (C) | LastUpdated (ISO 8601).
    """,
    Kernel = kernel
};

// ====================================================================================================================
// Helpers (parsing)
// ====================================================================================================================
static string ExtractFirstJsonObject(string text)
{
    int start = text.IndexOf('{');
    int end = text.LastIndexOf('}');
    if (start < 0 || end < 0 || end <= start) return text;
    return text[start..(end + 1)];
}

static bool TryParsePlace(string text, out PlaceRecommendation place)
{
    var jsonCaseInsensitive = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    string json = ExtractFirstJsonObject(text);
    try
    {
        if (JsonSerializer.Deserialize<PlaceRecommendation>(json, jsonCaseInsensitive) is { Name: not null, Location: not null } parsed
            && !string.IsNullOrWhiteSpace(parsed.Name)
            && !string.IsNullOrWhiteSpace(parsed.Location))
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
            Name = parts[0],
            Location = string.Join(' ', parts[1..^2]),
            Lat = lat,
            Lon = lon
        };
        return true;
    }

    place = new PlaceRecommendation { Name = "Unknown", Location = "Madeira", Lat = 0, Lon = 0 };
    return false;
}

// ====================================================================================================================
// Helpers (database)
// ====================================================================================================================
static async Task EnsureLocationsSchemaAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
{
    try
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE Locations ADD COLUMN Temperature REAL NOT NULL DEFAULT 0",
            cancellationToken);
        DebugUtil.Log("DB schema updated: added Locations.Temperature column.");
    }
    catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
    {
        DebugUtil.Log("DB schema already includes Locations.Temperature column.");
    }
}

static async Task EnsureLocationsSeededAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
{
    if (await db.Locations.AnyAsync(cancellationToken))
    {
        DebugUtil.Log("DB seed skipped (Locations already has rows).");
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
    DebugUtil.Log("DB seeded with default Madeira locations.");
}

static async Task RefreshAllTemperaturesAsync(LocationsDbContext db, ApiService apiService, CancellationToken cancellationToken = default)
{
    var locations = await db.Locations.ToListAsync(cancellationToken);
    if (locations.Count == 0)
    {
        return;
    }

    var invariant = CultureInfo.InvariantCulture;
    int updated = 0;
    int missing = 0;
    int failed = 0;

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
                location.Weather = temp.Value.ToString("0.#", invariant) + "°C";
                updated++;
            }
            else
            {
                missing++;
            }
        }
        catch (Exception ex)
        {
            failed++;
            DebugUtil.Log($"Weather refresh failed for '{location.Name ?? "(null)"}': {ex.Message}");
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    DebugUtil.Log($"Weather refresh complete. updated={updated}, missing={missing}, failed={failed}");
}

static string BuildLocationsTable(IReadOnlyList<LocationRow> rows)
{
    const string colLocation = "Location";
    const string colName = "Name";
    const string colLat = "Latitude";
    const string colLon = "Longitude";
    const string colTemp = "Temperature (C)";
    const string colUpdated = "LastUpdated";

    var invariant = CultureInfo.InvariantCulture;
    string FormatNumber(double value) => value.ToString(invariant);
    string FormatTemp(double temp) => temp.ToString("0.#", invariant) + "°C";
    string FormatUpdated(DateTime dt) => dt == default ? string.Empty : dt.ToString("O", invariant);

    int wLocation = colLocation.Length;
    int wName = colName.Length;
    int wLat = colLat.Length;
    int wLon = colLon.Length;
    int wTemp = colTemp.Length;
    int wUpdated = colUpdated.Length;

    foreach (var row in rows)
    {
        wLocation = Math.Max(wLocation, row.Location.Length);
        wName = Math.Max(wName, (row.Name ?? string.Empty).Length);
        wLat = Math.Max(wLat, FormatNumber(row.Latitude).Length);
        wLon = Math.Max(wLon, FormatNumber(row.Longitude).Length);
        wTemp = Math.Max(wTemp, FormatTemp(row.TemperatureC).Length);
        wUpdated = Math.Max(wUpdated, FormatUpdated(row.LastUpdated).Length);
    }

    static string Pad(string? text, int width) => (text ?? string.Empty).PadRight(width);
    static string Dashes(int width) => new('-', width);

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"{Pad(colLocation, wLocation)} | {Pad(colName, wName)} | {Pad(colLat, wLat)} | {Pad(colLon, wLon)} | {Pad(colTemp, wTemp)} | {Pad(colUpdated, wUpdated)}");
    sb.AppendLine($"{Dashes(wLocation)}-|-{Dashes(wName)}-|-{Dashes(wLat)}-|-{Dashes(wLon)}-|-{Dashes(wTemp)}-|-{Dashes(wUpdated)}");

    foreach (var row in rows)
    {
        sb.AppendLine(
            $"{Pad(row.Location, wLocation)} | {Pad(row.Name, wName)} | {Pad(FormatNumber(row.Latitude), wLat)} | {Pad(FormatNumber(row.Longitude), wLon)} | {Pad(FormatTemp(row.TemperatureC), wTemp)} | {Pad(FormatUpdated(row.LastUpdated), wUpdated)}");
    }

    return sb.ToString();
}

static string ExtractSingleTable(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return string.Empty;
    }

    // Prefer last fenced code block, if present.
    const string fence = "```";
    int lastFenceStart = text.LastIndexOf(fence, StringComparison.Ordinal);
    if (lastFenceStart >= 0)
    {
        int lastFenceEnd = text.LastIndexOf(fence, lastFenceStart - 1 >= 0 ? lastFenceStart - 1 : 0, StringComparison.Ordinal);
        if (lastFenceEnd >= 0)
        {
            // lastFenceEnd points to opening fence, lastFenceStart points to closing fence
            int open = lastFenceEnd + fence.Length;
            int close = lastFenceStart;
            var block = text[open..close].Trim();
            // Remove optional language tag at the start of the block.
            int firstNewline = block.IndexOf('\n');
            if (firstNewline > 0 && firstNewline < 20)
            {
                var firstLine = block[..firstNewline].Trim();
                if (firstLine.All(char.IsLetterOrDigit))
                {
                    block = block[(firstNewline + 1)..].Trim();
                }
            }

            if (block.Contains('|', StringComparison.Ordinal))
            {
                return block;
            }
        }
    }

    // Fallback: find the first table header and return from there.
    var normalized = text.Replace("\r\n", "\n");
    int header = normalized.IndexOf("Location |", StringComparison.OrdinalIgnoreCase);
    if (header < 0)
    {
        header = normalized.IndexOf("Location|", StringComparison.OrdinalIgnoreCase);
    }

    if (header >= 0)
    {
        return normalized[header..].Trim();
    }

    return text.Trim();
}

static async Task<string> InvokeAgentLastTextAsync(ChatCompletionAgent agent, string input, CancellationToken cancellationToken = default)
{
    string last = string.Empty;
    await foreach (var response in agent.InvokeAsync(input, cancellationToken: cancellationToken))
    {
        var content = response.Message.Content;
        if (!string.IsNullOrWhiteSpace(content))
        {
            last = content;
        }
    }

    return last.Trim();
}

// ====================================================================================================================
// Main loop (chat)
// ====================================================================================================================
do
{
    Console.Write("User > ");
    string? userInput = Console.ReadLine();
    if (userInput is null || userInput.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    DebugUtil.Log($"User input: {DebugUtil.Truncate(userInput.Trim(), 200)}");

    var ideasOutput = string.Empty;
    var interpreterOutput = string.Empty;

    await using var db = CreateDb();

    await EnsureLocationsSeededAsync(db);
    await RefreshAllTemperaturesAsync(db, apiService);

    DebugUtil.Log("Invoking Ideas agent...");
    ideasOutput = await InvokeAgentLastTextAsync(ideasAgent, userInput);

    DebugUtil.Log($"Ideas raw output: {DebugUtil.Truncate(ideasOutput, 400)}");

    if (!TryParsePlace(ideasOutput, out var place))
    {
        Console.WriteLine("Assistant > Não consegui interpretar as coordenadas. Tenta novamente.");
        DebugUtil.Log("TryParsePlace failed.");
        continue;
    }

    DebugUtil.Log($"Parsed place: name='{place.Name}', location='{place.Location}', lat={place.Lat}, lon={place.Lon}");

    WeatherApiResponse? weatherApiResponse;
    try
    {
        weatherApiResponse = await apiService.SearchWeather(place.Lat, place.Lon);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Assistant > Erro ao chamar a API do tempo: " + ex.Message);
        DebugUtil.Log($"Weather API error for parsed place: {ex}");
        continue;
    }

    var temperature = weatherApiResponse?.main?.temp;
    if (temperature is null)
    {
        Console.WriteLine("Assistant > Não consegui obter a temperatura atual. Tenta novamente.");
        DebugUtil.Log("Weather API response missing temperature.");
        continue;
    }

    DebugUtil.Log($"Weather API temperature for parsed place: {temperature.Value.ToString(CultureInfo.InvariantCulture)} C");

    var weatherInput = JsonSerializer.Serialize(new { temp = temperature.Value });
    var weatherText = await InvokeAgentLastTextAsync(weatherAgent, weatherInput);
    if (string.IsNullOrWhiteSpace(weatherText))
    {
        weatherText = temperature.Value.ToString("0.#", CultureInfo.InvariantCulture) + "°C";
    }

    var now = DateTime.UtcNow;
    var placeName = (place.Name ?? "Unknown").Trim();
    var placeNameLower = placeName.ToLower();

    var locationEntity = await db.Locations.FirstOrDefaultAsync(l => l.Name != null && l.Name.ToLower() == placeNameLower);
    if (locationEntity is null)
    {
        DebugUtil.Log($"DB upsert: inserting new location '{placeName}'.");
        locationEntity = new Location { Type = "unknown", Weather = "N/A" };
        db.Locations.Add(locationEntity);
    }
    else
    {
        DebugUtil.Log($"DB upsert: updating location '{locationEntity.Name}' -> '{placeName}'.");
    }

    locationEntity.Name = placeName;
    locationEntity.Latitude = place.Lat;
    locationEntity.Longitude = place.Lon;
    locationEntity.Temperature = temperature.Value;
    locationEntity.Weather = weatherText;
    locationEntity.LastUpdated = now;

    await db.SaveChangesAsync();

    var locationsSnapshot = await db.Locations.AsNoTracking()
        .OrderBy(l => l.Name)
        .Select(l => new LocationRow(
            "Madeira",
            l.Name,
            l.Latitude,
            l.Longitude,
            l.Temperature,
            l.LastUpdated))
        .ToListAsync();

    locationsSnapshot.RemoveAll(r => string.Equals(r.Name, "Curral das Freiras", StringComparison.OrdinalIgnoreCase));

    DebugUtil.Log($"Locations snapshot rows: {locationsSnapshot.Count}");

    DebugUtil.Log("Interpreter agent: formatting table...");
    var interpreterInput = JsonSerializer.Serialize(locationsSnapshot, new JsonSerializerOptions { WriteIndented = true });
    var interpreterRaw = await InvokeAgentLastTextAsync(interpreterAgent, interpreterInput);
    interpreterOutput = ExtractSingleTable(interpreterRaw);
    if (string.IsNullOrWhiteSpace(interpreterOutput) || !interpreterOutput.Contains('|', StringComparison.Ordinal))
    {
        DebugUtil.Log("Interpreter agent output invalid; falling back to C# formatter.");
        interpreterOutput = BuildLocationsTable(locationsSnapshot);
    }

    DebugUtil.Log($"Interpreter output: {DebugUtil.Truncate(interpreterOutput, 400)}");
    Console.WriteLine(interpreterOutput);
} while (true);

// ====================================================================================================================
// Models
// ====================================================================================================================
public sealed class PlaceRecommendation
{
    public string? Name { get; init; }
    public string? Location { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
}

file sealed record LocationRow(
    string Location,
    string? Name,
    double Latitude,
    double Longitude,
    double TemperatureC,
    DateTime LastUpdated);
