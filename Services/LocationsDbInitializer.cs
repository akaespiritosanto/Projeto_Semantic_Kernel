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
                    DbScripts.Load("Locations/20260408_01_DDL_add_temperature_column_to_locations.sql"),
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
                await db.Database.ExecuteSqlRawAsync(DbScripts.Load("Locations/20260408_02_DDL_drop_type_column_from_locations.sql"), cancellationToken);
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

        await db.Database.ExecuteSqlRawAsync(DbScripts.Load("Locations/20260408_03_DDL_drop_locations_new_table_if_exists.sql"), cancellationToken);

        await db.Database.ExecuteSqlRawAsync(DbScripts.Load("Locations/20260408_04_DDL_create_locations_new_table.sql"), cancellationToken);

        await db.Database.ExecuteSqlRawAsync(DbScripts.Load("Locations/20260408_05_DML_copy_locations_to_locations_new.sql"), cancellationToken);

        await db.Database.ExecuteSqlRawAsync(DbScripts.Load("Locations/20260408_06_DDL_drop_locations_table.sql"), cancellationToken);
        await db.Database.ExecuteSqlRawAsync(DbScripts.Load("Locations/20260408_07_DDL_rename_locations_new_to_locations.sql"), cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }
}

