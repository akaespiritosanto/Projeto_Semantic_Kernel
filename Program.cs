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
// Auto-testes (sem dependências externas)
// Executar com: `dotnet run -- --self-test`
// ====================================================================================================================
if (Array.Exists(args, a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
{
    var failures = SelfTests.Run();
    Environment.ExitCode = failures == 0 ? 0 : 1;
    return;
}

// ====================================================================================================================
// Configuração do Kernel / LLM
// ====================================================================================================================
var builder = Kernel.CreateBuilder()
    .AddOllamaChatCompletion(
        modelId: "llama3.1:latest",
        endpoint: new Uri("http://localhost:11434")
);

// ====================================================================================================================
// Variáveis de ambiente
// ====================================================================================================================
Env.Load();
DebugUtil.Log("Loaded environment (.env).");

var kernel = builder.Build();
DebugUtil.Log("Semantic Kernel built.");

// ====================================================================================================================
// HTTP + Base de dados (SQLite)
// ====================================================================================================================
var locationsDbPath = Path.Combine(AppContext.BaseDirectory, "locations.db");
DebugUtil.Log($"SQLite DB path: {locationsDbPath}");
var dbOptions = new DbContextOptionsBuilder<LocationsDbContext>()
    .UseSqlite($"Data Source={locationsDbPath}")
    .Options;

using var httpClient = new HttpClient();
var apiService = new ApiService(httpClient);

LocationsDbContext CreateDb() => new(dbOptions);

// ====================================================================================================================
// Inicialização da base de dados (criar, ajustar schema e fazer seed)
// ====================================================================================================================
await using (var db = CreateDb())
{
    db.Database.EnsureCreated();
    await EnsureLocationsSchemaAsync(db);
    await EnsureLocationsSeededAsync(db);
    await RefreshAllTemperaturesAsync(db, apiService);
    DebugUtil.Log($"DB ready. Locations count: {await db.Locations.CountAsync()}");
}

// ====================================================================================================================
// Agentes (Semantic Kernel)
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
    You will receive JSON with:
    - selectedName: the place name the user asked about
    - rows: an array of locations with temperature and last updated

    Your job:
    1) Find the row in rows whose Name matches selectedName (case-insensitive).
    2) Output ONLY a short final message to the user in Portuguese based on that row (no table, no code fences).
       Example format: "Para {Name} ({Location}), a temperatura atual é {TemperatureC}°C (atualizado em {LastUpdated})."
    3) If no matching row is found, say you couldn't find that location in the table.
    """,
    Kernel = kernel
};

// ====================================================================================================================
// Funções auxiliares (parsing)
// ====================================================================================================================
// Nota: parsing movido para `AppLogic.cs` para ser fácil de testar.

// ====================================================================================================================
// Funções auxiliares (base de dados)
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

// ====================================================================================================================
// Funções auxiliares (formatação)
// ====================================================================================================================
static string BuildLocationsTable(IReadOnlyList<LocationRow> rows) =>
    AppLogic.BuildLocationsTable(rows);

// ====================================================================================================================
// Funções auxiliares (chamadas a agentes)
// ====================================================================================================================
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
// Loop principal (chat)
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

    // ====================================================================================================================
    // 1) Abrir DB e manter dados coerentes (seed + refresh de temperaturas)
    // ====================================================================================================================
    await using var db = CreateDb();
    await EnsureLocationsSeededAsync(db);
    await RefreshAllTemperaturesAsync(db, apiService);

    // ====================================================================================================================
    // 2) Pedir ao agente "Ideas" uma recomendação (JSON)
    // ====================================================================================================================
    DebugUtil.Log("Invoking Ideas agent...");
    var ideasOutput = await InvokeAgentLastTextAsync(ideasAgent, userInput);

    DebugUtil.Log($"Ideas raw output: {DebugUtil.Truncate(ideasOutput, 400)}");

    if (!AppLogic.TryParsePlace(ideasOutput, out var place))
    {
        Console.WriteLine("Assistant > Não consegui interpretar as coordenadas. Tenta novamente.");
        DebugUtil.Log("TryParsePlace failed.");
        continue;
    }

    DebugUtil.Log($"Parsed place: name='{place.Name}', location='{place.Location}', lat={place.Lat}, lon={place.Lon}");

    // ====================================================================================================================
    // 3) Obter temperatura via API e (opcionalmente) formatar com o agente "Weather"
    // ====================================================================================================================
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

    // ====================================================================================================================
    // 4) Guardar/atualizar a localização no SQLite (upsert simples por nome)
    // ====================================================================================================================
    var now = DateTime.UtcNow;
    var placeName = (place.Name ?? "Unknown").Trim();
    var placeNameLower = placeName.ToLowerInvariant();

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

    // ====================================================================================================================
    // 5) Criar snapshot para imprimir a tabela (texto alinhado)
    // ====================================================================================================================
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

    var tableOutput = BuildLocationsTable(locationsSnapshot);
    DebugUtil.Log($"Table output: {DebugUtil.Truncate(tableOutput, 400)}");
    Console.WriteLine(tableOutput);

    // ====================================================================================================================
    // 6) Mensagem final com base na tabela (agente "Interpreter" + fallback local)
    // ====================================================================================================================
    DebugUtil.Log("Interpreter agent: generating final message...");
    string finalMessage = string.Empty;
    try
    {
        var finalInput = JsonSerializer.Serialize(new { selectedName = placeName, rows = locationsSnapshot }, new JsonSerializerOptions { WriteIndented = true });
        var finalMessageRaw = await InvokeAgentLastTextAsync(interpreterAgent, finalInput);
        finalMessage = (finalMessageRaw ?? string.Empty).Trim();
    }
    catch (Exception ex)
    {
        DebugUtil.Log($"Interpreter agent failed: {ex.Message}");
    }

    if (string.IsNullOrWhiteSpace(finalMessage))
    {
        var row = locationsSnapshot.FirstOrDefault(r => string.Equals(r.Name, placeName, StringComparison.OrdinalIgnoreCase));
        if (row is not null && !string.IsNullOrWhiteSpace(row.Name))
        {
            var invariant = CultureInfo.InvariantCulture;
            finalMessage = $"Para {row.Name} ({row.Location}), a temperatura atual é {row.TemperatureC.ToString("0.##", invariant)}°C (atualizado em {row.LastUpdated.ToString("O", invariant)}).";
        }
        else
        {
            finalMessage = "Não consegui encontrar essa localização na tabela.";
        }
    }

    Console.WriteLine();
    Console.WriteLine(finalMessage);
} while (true);

// ====================================================================================================================
// Modelos (DTOs internos)
// ====================================================================================================================
public sealed class PlaceRecommendation
{
    public string? Name { get; init; }
    public string? Location { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
}

internal sealed record LocationRow(
    string Location,
    string? Name,
    double Latitude,
    double Longitude,
    double TemperatureC,
    DateTime LastUpdated);
