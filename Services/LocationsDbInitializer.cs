using Microsoft.EntityFrameworkCore;
using semantic_kernel.Models;
using System.Data;

namespace semantic_kernel.Services;

internal static class LocationsDbInitializer
{
    public static async Task EnsureLocationsSchemaAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
    {
        var columns = await GetLocationsColumnsAsync(db, cancellationToken);

        if (!columns.Contains("Temperature"))
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

        if (columns.Contains("Type"))
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Locations DROP COLUMN Type", cancellationToken);
                DebugUtil.Log("DB schema updated: dropped legacy Locations.Type column.");
            }
            catch (Exception ex)
            {
                DebugUtil.Log($"DB schema: DROP COLUMN Type failed ({ex.Message}). Rebuilding Locations table...");
                await RebuildLocationsTableWithoutTypeAsync(db, cancellationToken);
                DebugUtil.Log("DB schema updated: rebuilt Locations table without Type column.");
            }
        }
    }

    public static async Task EnsureLocationsSeededAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Locations.AnyAsync(cancellationToken))
        {
            DebugUtil.Log("DB seed skipped (Locations already has rows).");
            return;
        }

        db.Locations.AddRange(
            new Location { Name = "Funchal", Latitude = 32.6669, Longitude = -16.9241, Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue },
            new Location { Name = "Pico do Areeiro", Latitude = 32.7356, Longitude = -16.9289, Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue },
            new Location { Name = "Porto Moniz", Latitude = 32.8668, Longitude = -17.1662, Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue },
            new Location { Name = "Santana", Latitude = 32.8007, Longitude = -16.8801, Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue },
            new Location { Name = "Ponta de São Lourenço", Latitude = 32.7403, Longitude = -16.7014, Weather = "N/A", Temperature = 0, LastUpdated = DateTime.MinValue }
        );

        await db.SaveChangesAsync(cancellationToken);
        DebugUtil.Log("DB seeded with default Madeira locations.");
    }

    private static async Task<HashSet<string>> GetLocationsColumnsAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info('Locations')";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                columns.Add(name);
            }
        }

        return columns;
    }

    private static async Task RebuildLocationsTableWithoutTypeAsync(LocationsDbContext db, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Locations_new", cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE Locations_new (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Latitude REAL NOT NULL,
                Longitude REAL NOT NULL,
                Weather TEXT NOT NULL,
                Temperature REAL NOT NULL DEFAULT 0,
                LastUpdated TEXT NOT NULL
            )
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Locations_new (Id, Name, Latitude, Longitude, Weather, Temperature, LastUpdated)
            SELECT
                Id,
                Name,
                Latitude,
                Longitude,
                COALESCE(Weather, 'N/A'),
                COALESCE(Temperature, 0),
                COALESCE(LastUpdated, '0001-01-01T00:00:00.0000000')
            FROM Locations
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync("DROP TABLE Locations", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Locations_new RENAME TO Locations", cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }
}

