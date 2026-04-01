using System.Globalization;
using System.Linq;

// ====================================================================================================================
// Auto-testes simples (sem dependências externas)
// - Executa com: `dotnet run -- --self-test`
// ====================================================================================================================
internal static class SelfTests
{
    public static int Run()
    {
        int failures = 0;

        void Assert(bool condition, string message)
        {
            if (condition)
            {
                return;
            }

            failures++;
            Console.Error.WriteLine("FAIL: " + message);
        }

        // ====================================================================================================================
        // ExtractFirstJsonObject
        // ====================================================================================================================
        Assert(AppLogic.ExtractFirstJsonObject("no json here") == "no json here", "ExtractFirstJsonObject returns original text when no braces exist.");
        Assert(AppLogic.ExtractFirstJsonObject("x {\"a\":1} y") == "{\"a\":1}", "ExtractFirstJsonObject extracts the first JSON object.");

        // ====================================================================================================================
        // TryParsePlace (JSON)
        // ====================================================================================================================
        Assert(
            AppLogic.TryParsePlace("{\"name\":\"Pico do Areeiro\",\"location\":\"Madeira\",\"lat\":32.735,\"lon\":-16.928}", out var jsonPlace)
            && jsonPlace.Name == "Pico do Areeiro"
            && jsonPlace.Location == "Madeira"
            && Math.Abs(jsonPlace.Lat - 32.735) < 0.000001
            && Math.Abs(jsonPlace.Lon - (-16.928)) < 0.000001,
            "TryParsePlace parses JSON output.");

        // ====================================================================================================================
        // TryParsePlace (fallback: "Name Location lat lon")
        // ====================================================================================================================
        Assert(
            AppLogic.TryParsePlace("Funchal Madeira 32.6669 -16.9241", out var textPlace)
            && textPlace.Name == "Funchal"
            && textPlace.Location == "Madeira"
            && Math.Abs(textPlace.Lat - 32.6669) < 0.000001
            && Math.Abs(textPlace.Lon - (-16.9241)) < 0.000001,
            "TryParsePlace parses fallback text output.");

        // ====================================================================================================================
        // BuildLocationsTable (alinhamento das colunas)
        // ====================================================================================================================
        var rows = new List<LocationRow>
        {
            new("Madeira", "Funchal", 32.6669, -16.9241, 16.1, DateTime.Parse("2026-03-31T20:33:20.8179028Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)),
            new("Madeira", "Pico do Areeiro", 32.7356, -16.9289, 7.1, DateTime.Parse("2026-03-31T20:33:20.8720101Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)),
        };

        var table = AppLogic.BuildLocationsTable(rows);
        var lines = table.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert(lines.Length >= 3, "BuildLocationsTable outputs header, separator, and at least one row.");

        var headerLine = lines[0];
        var pipePositions = headerLine
            .Select((ch, idx) => (ch, idx))
            .Where(x => x.ch == '|')
            .Select(x => x.idx)
            .ToArray();

        Assert(pipePositions.Length >= 5, "BuildLocationsTable header contains all column separators.");

        foreach (var line in lines.Skip(1))
        {
            foreach (var pos in pipePositions)
            {
                Assert(pos < line.Length && line[pos] == '|', "BuildLocationsTable keeps columns aligned across all rows.");
            }
        }

        if (failures == 0)
        {
            Console.WriteLine("Self-tests: OK");
        }

        return failures;
    }
}
