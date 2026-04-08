using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using semantic_kernel;
using semantic_kernel.Dtos;
using semantic_kernel.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
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
// Variáveis de ambiente
// ====================================================================================================================
LoadDotEnvPreferNonEmpty();
DebugUtil.Log("Loaded environment (.env) when present.");

static void LoadDotEnvPreferNonEmpty()
{
    // Why: docker-compose can create empty env vars (e.g. `FOO: ${FOO}` when host var is missing),
    // and those empty vars should NOT block values from `.env`.
    // Rule: only set vars from `.env` when the current value is missing OR whitespace.
    var envPath = FindDotEnvPath();
    if (envPath is null)
    {
        DebugUtil.Log("No .env found (skipping).");
        return;
    }

    int applied = 0;
    foreach (var (key, value) in ParseDotEnvFile(envPath))
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            continue;
        }

        var current = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(current))
        {
            Environment.SetEnvironmentVariable(key, value);
            applied++;
        }
    }

    DebugUtil.Log($".env loaded from '{envPath}'. Applied {applied} variable(s).");
}

static string? FindDotEnvPath()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    for (int depth = 0; depth < 20 && dir is not null; depth++)
    {
        var candidate = Path.Combine(dir.FullName, ".env");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        dir = dir.Parent;
    }

    return null;
}

static IEnumerable<(string Key, string Value)> ParseDotEnvFile(string path)
{
    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var idx = line.IndexOf('=');
        if (idx <= 0)
        {
            continue;
        }

        var key = line[..idx].Trim();
        var value = line[(idx + 1)..].Trim();

        if (value.Length >= 2
            && ((value[0] == '\"' && value[^1] == '\"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        yield return (key, value);
    }
}

static string GetEnvOrDefault(string name, string defaultValue, string exampleForDocs)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (!string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    DebugUtil.Log($"Env '{name}' not set. Using default: {defaultValue}. (Example: {exampleForDocs})");
    return defaultValue;
}

static string GetOllamaEndpoint()
{
    var explicitValue = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT");
    if (!string.IsNullOrWhiteSpace(explicitValue))
    {
        return explicitValue;
    }

    var fallbackVarName = IsRunningInContainer() ? "OLLAMA_ENDPOINT_DOCKER" : "OLLAMA_ENDPOINT_LOCAL";
    var fallback = Environment.GetEnvironmentVariable(fallbackVarName);
    if (!string.IsNullOrWhiteSpace(fallback))
    {
        return fallback;
    }

    // Safe defaults for first-run experience (can always be overridden by env vars).
    return IsRunningInContainer()
        ? "http://host.docker.internal:11434"
        : "http://localhost:11434";
}

// ====================================================================================================================
// Configuração do Kernel / LLM
// ====================================================================================================================
var ollamaEndpoint = GetOllamaEndpoint();
var ollamaModel = GetEnvOrDefault("OLLAMA_MODEL", "llama3.1:latest", "OLLAMA_MODEL=llama3.1:latest");

var builder = Kernel.CreateBuilder()
    .AddOllamaChatCompletion(
        modelId: ollamaModel,
        endpoint: new Uri(ollamaEndpoint)
);

var kernel = builder.Build();
DebugUtil.Log("Semantic Kernel built.");

// ====================================================================================================================
// HTTP + Base de dados (SQLite)
// ====================================================================================================================
var locationsDbConnectionString = GetEnvOrDefault(
    "LOCATIONS_DB_CONNECTION_STRING",
    "Data Source=locations.db",
    "LOCATIONS_DB_CONNECTION_STRING=Data Source=locations.db");

static void EnsureSqliteDirectoryExists(string sqliteConnectionString)
{
    try
    {
        var csb = new SqliteConnectionStringBuilder(sqliteConnectionString);
        var dataSource = csb.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dir = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
    catch (Exception ex)
    {
        DebugUtil.Log($"SQLite: could not validate/create directory from connection string ({ex.Message}).");
    }
}

EnsureSqliteDirectoryExists(locationsDbConnectionString);
try
{
    var csb = new SqliteConnectionStringBuilder(locationsDbConnectionString);
    var dataSource = csb.DataSource;
    if (!string.IsNullOrWhiteSpace(dataSource) && !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
    {
        var full = Path.IsPathRooted(dataSource) ? dataSource : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), dataSource));
        DebugUtil.Log($"SQLite DB path: {full}");
    }
    else
    {
        DebugUtil.Log("SQLite DB path: (not a file path)");
    }
}
catch
{
    DebugUtil.Log("SQLite DB path: (unavailable)");
}
var dbOptions = new DbContextOptionsBuilder<LocationsDbContext>()
    .UseSqlite(locationsDbConnectionString)
    .Options;

using var httpClient = new HttpClient();
var apiService = new ApiService(httpClient);

LocationsDbContext CreateDb() => new(dbOptions);

// ====================================================================================================================
// Inicialização da base de dados
// - Cria o ficheiro SQLite se não existir
// - Garante que o schema está atualizado (ex: remove colunas legadas)
// - Faz seed inicial (localizações default) quando a tabela está vazia
// - Atualiza as temperaturas para a tabela ficar útil logo no arranque
// ====================================================================================================================
await EnsureDatabaseReadyAsync(CreateDb, apiService);

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
       If LastUpdated is missing/default (e.g. "0001-01-01..."), say the temperature is not available yet and mention API_KEY is needed.
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
// Nota: migração de schema e seed foram movidos para `Services/LocationsDbInitializer.cs` para manter este ficheiro
// (Program.cs) focado no fluxo principal da aplicação.

static async Task EnsureDatabaseReadyAsync(Func<LocationsDbContext> createDb, ApiService apiService, CancellationToken cancellationToken = default)
{
    await using var db = createDb();

    // `EnsureCreated` é suficiente para este projeto (não estamos a usar migrations EF).
    db.Database.EnsureCreated();

    // Mantém o schema e dados base coerentes, mesmo que o ficheiro `locations.db` já exista de execuções anteriores.
    await EnsureDatabaseConsistentAsync(db, apiService, cancellationToken);

    DebugUtil.Log($"DB ready. Locations count: {await db.Locations.CountAsync(cancellationToken)}");
}

static async Task EnsureDatabaseConsistentAsync(LocationsDbContext db, ApiService apiService, CancellationToken cancellationToken = default)
{
    await LocationsDbInitializer.EnsureLocationsSchemaAsync(db, cancellationToken);
    await LocationsDbInitializer.EnsureLocationsSeededAsync(db, cancellationToken);

    // Best-effort: se falhar (ex: sem API_KEY), o erro fica no log e a app continua.
    await RefreshAllTemperaturesAsync(db, apiService, cancellationToken);
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

static async Task<string> FormatTemperatureAsync(ChatCompletionAgent weatherAgent, double temperatureC, CancellationToken cancellationToken = default)
{
    // O agente "Weather" serve apenas para devolver a temperatura num formato curto. Se falhar, fazemos fallback local.
    var weatherInput = JsonSerializer.Serialize(new { temp = temperatureC });
    string weatherText = string.Empty;
    try
    {
        weatherText = await InvokeAgentLastTextAsync(weatherAgent, weatherInput, cancellationToken);
    }
    catch (Exception ex)
    {
        DebugUtil.Log($"Weather agent failed (fallback to local formatting): {ex.Message}");
    }
    if (string.IsNullOrWhiteSpace(weatherText))
    {
        weatherText = temperatureC.ToString("0.#", CultureInfo.InvariantCulture) + "°C";
    }

    return weatherText;
}

static async Task UpsertLocationAsync(
    LocationsDbContext db,
    PlaceRecommendation place,
    double? temperatureC,
    string? weatherText,
    DateTime nowUtc,
    CancellationToken cancellationToken = default)
{
    var placeName = (place.Name ?? "Unknown").Trim();
    var placeNameLower = placeName.ToLowerInvariant();

    var locationEntity = await db.Locations.FirstOrDefaultAsync(
        l => l.Name != null && l.Name.ToLower() == placeNameLower,
        cancellationToken);

    if (locationEntity is null)
    {
        DebugUtil.Log($"DB upsert: inserting new location '{placeName}'.");
        locationEntity = new Location { Weather = "N/A" };
        db.Locations.Add(locationEntity);
    }
    else
    {
        DebugUtil.Log($"DB upsert: updating location '{locationEntity.Name}' -> '{placeName}'.");
    }

    locationEntity.Name = placeName;
    locationEntity.Latitude = place.Lat;
    locationEntity.Longitude = place.Lon;

    if (temperatureC is not null && !string.IsNullOrWhiteSpace(weatherText) && nowUtc != default)
    {
        locationEntity.Temperature = temperatureC.Value;
        locationEntity.Weather = weatherText;
        locationEntity.LastUpdated = nowUtc;
    }
    else if (locationEntity.Id == 0)
    {
        locationEntity.Temperature = 0;
        locationEntity.Weather = "N/A";
        locationEntity.LastUpdated = default;
    }

    await db.SaveChangesAsync(cancellationToken);
}

static async Task<List<LocationRow>> BuildLocationsSnapshotAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
{
    const string locationName = "Madeira";

    var tz = GetDisplayTimeZone();

    // Snapshot é "read-only" (AsNoTracking) porque serve apenas para imprimir/interpretar o estado atual.
    var raw = await db.Locations.AsNoTracking()
        .OrderBy(l => l.Name)
        .Select(l => new { l.Name, l.Latitude, l.Longitude, l.Temperature, l.LastUpdated })
        .ToListAsync(cancellationToken);

    var rows = raw.Select(l => new LocationRow(
            locationName,
            l.Name,
            l.Latitude,
            l.Longitude,
            l.Temperature,
            ConvertUtcToDisplayTime(l.LastUpdated, tz)))
        .ToList();

    // Regra de negócio temporária (mantida para não alterar comportamento).
    rows.RemoveAll(r => string.Equals(r.Name, "Curral das Freiras", StringComparison.OrdinalIgnoreCase));

    return rows;
}

static TimeZoneInfo GetDisplayTimeZone()
{
    // Prefer explicit app setting, fallback to standard `TZ`, then OS default.
    var id = Environment.GetEnvironmentVariable("APP_TIMEZONE");
    if (string.IsNullOrWhiteSpace(id))
    {
        id = Environment.GetEnvironmentVariable("TZ");
    }

    if (string.IsNullOrWhiteSpace(id))
    {
        return TimeZoneInfo.Local;
    }

    try
    {
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }
    catch (Exception ex)
    {
        DebugUtil.Log($"Timezone '{id}' not found ({ex.Message}). Using system local timezone.");
        return TimeZoneInfo.Local;
    }
}

static DateTimeOffset ConvertUtcToDisplayTime(DateTime utcFromDb, TimeZoneInfo tz)
{
    if (utcFromDb == default)
    {
        return default;
    }

    // SQLite returns Kind=Unspecified even if we stored UTC.
    var utc = DateTime.SpecifyKind(utcFromDb, DateTimeKind.Utc);
    var utcOffset = new DateTimeOffset(utc);
    return TimeZoneInfo.ConvertTime(utcOffset, tz);
}

static async Task<string> BuildFinalMessageAsync(
    ChatCompletionAgent interpreterAgent,
    string selectedName,
    IReadOnlyList<LocationRow> rows,
    CancellationToken cancellationToken = default)
{
    var row = rows.FirstOrDefault(r => string.Equals(r.Name, selectedName, StringComparison.OrdinalIgnoreCase));
    if (row is not null && !string.IsNullOrWhiteSpace(row.Name) && row.LastUpdated == default)
    {
        return $"Para {row.Name} ({row.Location}), ainda não tenho a temperatura (define API_KEY no .env para ativar o tempo).";
    }

    // Tentamos gerar a frase com o agente "Interpreter". Se falhar, fazemos fallback local.
    try
    {
        var finalInput = JsonSerializer.Serialize(
            new { selectedName, rows },
            new JsonSerializerOptions { WriteIndented = true });

        var finalMessageRaw = await InvokeAgentLastTextAsync(interpreterAgent, finalInput, cancellationToken);
        var finalMessage = (finalMessageRaw ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(finalMessage))
        {
            return finalMessage;
        }
    }
    catch (Exception ex)
    {
        DebugUtil.Log($"Interpreter agent failed: {ex.Message}");
    }

    if (row is not null && !string.IsNullOrWhiteSpace(row.Name))
    {
        var invariant = CultureInfo.InvariantCulture;
        return $"Para {row.Name} ({row.Location}), a temperatura atual é {row.TemperatureC.ToString("0.##", invariant)}°C (atualizado em {row.LastUpdated.ToString("O", invariant)}).";
    }

    return "Não consegui encontrar essa localização na tabela.";
}

static async Task RunChatLoopAsync(
    Func<LocationsDbContext> createDb,
    ApiService apiService,
    ChatCompletionAgent ideasAgent,
    ChatCompletionAgent weatherAgent,
    ChatCompletionAgent interpreterAgent,
    CancellationToken cancellationToken = default)
{
    while (true)
    {
        Console.Write("User > ");
        var userInputRaw = Console.ReadLine();
        if (userInputRaw is null)
        {
            if (IsRunningInContainer())
            {
                Console.WriteLine();
                Console.WriteLine("Assistant > Sem stdin interativo. Executa com `docker run -it ...` (ou `docker compose up`).");
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            break;
        }

        var userInput = userInputRaw.Trim();
        if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        if (string.IsNullOrWhiteSpace(userInput))
        {
            continue;
        }

        DebugUtil.Log($"User input: {DebugUtil.Truncate(userInput, 200)}");

        // ================================================================================================================
        // 1) Abrir DB e manter dados coerentes (schema + seed + refresh de temperaturas)
        // ================================================================================================================
        await using var db = createDb();
        await EnsureDatabaseConsistentAsync(db, apiService, cancellationToken);

        // ================================================================================================================
        // 2) Pedir ao agente "Ideas" uma recomendação (JSON)
        // ================================================================================================================
        DebugUtil.Log("Invoking Ideas agent...");
        string ideasOutput;
        try
        {
            ideasOutput = await InvokeAgentLastTextAsync(ideasAgent, userInput, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine("Assistant > Não consegui ligar ao Ollama. Confirma que o `ollama serve` está a correr e que o endpoint no .env está correto.");
            DebugUtil.Log($"Ollama connection failed (endpoint='{GetOllamaEndpoint()}'): {ex.Message}");
            continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Assistant > Erro ao chamar o Ollama: " + ex.Message);
            DebugUtil.Log($"Ideas agent failed: {ex}");
            continue;
        }
        DebugUtil.Log($"Ideas raw output: {DebugUtil.Truncate(ideasOutput, 400)}");

        if (!AppLogic.TryParsePlace(ideasOutput, out var place))
        {
            Console.WriteLine("Assistant > Não consegui interpretar as coordenadas. Tenta novamente.");
            DebugUtil.Log("TryParsePlace failed.");
            continue;
        }

        DebugUtil.Log($"Parsed place: name='{place.Name}', location='{place.Location}', lat={place.Lat}, lon={place.Lon}");

        // ================================================================================================================
        // 3) Obter temperatura via API e formatar com o agente "Weather"
        // ================================================================================================================
        WeatherApiResponse? weatherApiResponse;
        try
        {
            weatherApiResponse = await apiService.SearchWeather(place.Lat, place.Lon, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Assistant > Erro ao chamar a API do tempo: " + ex.Message);
            DebugUtil.Log($"Weather API error for parsed place: {ex}");
            continue;
        }

        var temperatureC = weatherApiResponse?.main?.temp;
        string? weatherText = null;
        var nowUtc = default(DateTime);

        if (temperatureC is null)
        {
            Console.WriteLine("Assistant > Sem API_KEY (ou sem resposta da API), não consigo obter a temperatura agora.");
            DebugUtil.Log("Weather API response missing temperature.");
        }
        else
        {
            nowUtc = DateTime.UtcNow;
            DebugUtil.Log($"Weather API temperature for parsed place: {temperatureC.Value.ToString(CultureInfo.InvariantCulture)} C");
            weatherText = await FormatTemperatureAsync(weatherAgent, temperatureC.Value, cancellationToken);
        }

        // ================================================================================================================
        // 4) Guardar/atualizar a localização no SQLite (upsert simples por nome)
        // ================================================================================================================
        await UpsertLocationAsync(db, place, temperatureC, weatherText, nowUtc, cancellationToken);

        // ================================================================================================================
        // 5) Criar snapshot para imprimir a tabela (texto alinhado)
        // ================================================================================================================
        var locationsSnapshot = await BuildLocationsSnapshotAsync(db, cancellationToken);
        DebugUtil.Log($"Locations snapshot rows: {locationsSnapshot.Count}");

        var tableOutput = AppLogic.BuildLocationsTable(locationsSnapshot);
        DebugUtil.Log($"Table output: {DebugUtil.Truncate(tableOutput, 400)}");
        Console.WriteLine(tableOutput);

        // ================================================================================================================
        // 6) Mensagem final com base na tabela (agente "Interpreter" + fallback local)
        // ================================================================================================================
        DebugUtil.Log("Interpreter agent: generating final message...");
        var selectedName = (place.Name ?? "Unknown").Trim();
        var finalMessage = await BuildFinalMessageAsync(interpreterAgent, selectedName, locationsSnapshot, cancellationToken);

        Console.WriteLine();
        Console.WriteLine(finalMessage);
    }
}

static bool IsRunningInContainer()
{
    var value = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
    return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

// ====================================================================================================================
// Loop principal (chat)
// ====================================================================================================================
await RunChatLoopAsync(CreateDb, apiService, ideasAgent, weatherAgent, interpreterAgent);

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
    DateTimeOffset LastUpdated);
