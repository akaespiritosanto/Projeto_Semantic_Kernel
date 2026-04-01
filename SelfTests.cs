using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using semantic_kernel.Models;
using semantic_kernel.Services;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// ====================================================================================================================
// Auto-testes simples (sem dependências externas)
// - Executa com: `dotnet run -- --self-test`
// - Inclui SQLite in-memory para validar migrações de schema
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

        // ====================================================================================================================
        // Locations schema migration (remove legacy Type column)
        // ====================================================================================================================
        Assert(typeof(Location).GetProperty("Type") is null, "Location no longer has a Type property.");

        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    """
                    CREATE TABLE Locations (
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        Latitude REAL NOT NULL,
                        Longitude REAL NOT NULL,
                        Type TEXT NOT NULL,
                        Weather TEXT NOT NULL,
                        LastUpdated TEXT NOT NULL
                    );
                    INSERT INTO Locations (Name, Latitude, Longitude, Type, Weather, LastUpdated)
                    VALUES ('LegacyRow', 1.0, 2.0, 'legacy', 'N/A', '2026-03-31T20:33:20.8179028Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            var options = new DbContextOptionsBuilder<LocationsDbContext>()
                .UseSqlite(conn)
                .Options;

            using var db = new LocationsDbContext(options);
            LocationsDbInitializer.EnsureLocationsSchemaAsync(db).GetAwaiter().GetResult();

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info('Locations')";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        columns.Add(name);
                    }
                }
            }

            Assert(!columns.Contains("Type"), "EnsureLocationsSchemaAsync removes legacy Locations.Type column.");
            Assert(columns.Contains("Temperature"), "EnsureLocationsSchemaAsync ensures Locations.Temperature column exists.");

            var migrated = db.Locations.AsNoTracking().Single();
            Assert(migrated.Name == "LegacyRow", "Schema migration preserves existing rows.");
            Assert(Math.Abs(migrated.Temperature - 0) < 0.000001, "Schema migration sets default Temperature for legacy rows.");
        }
        catch (Exception ex)
        {
            Assert(false, "Schema migration self-test failed: " + ex.Message);
        }

        if (failures == 0)
        {
            Console.WriteLine("Self-tests: OK");
        }

        return failures;
    }
}
