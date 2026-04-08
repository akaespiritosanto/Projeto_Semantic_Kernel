using System.Globalization;
using System.Text;
using System.Text.Json;

// ====================================================================================================================
// Lógica "pura" (sem I/O) da aplicação
// - Mantida separada para ser mais fácil de testar e ler
// ====================================================================================================================
internal static class AppLogic
{
    // ====================================================================================================================
    // Parsing (texto -> JSON -> PlaceRecommendation)
    // ====================================================================================================================
    public static string ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start)
        {
            return text;
        }

        return text[start..(end + 1)];
    }

    public static bool TryParsePlace(string text, out PlaceRecommendation place)
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

        string[] parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
    // Tabela (rows -> texto formatado)
    // ====================================================================================================================
    public static string BuildLocationsTable(IReadOnlyList<LocationRow> rows)
    {
        const string colLocation = "Location";
        const string colName = "Name";
        const string colLat = "Latitude";
        const string colLon = "Longitude";
        const string colTemp = "Temperature (C)";
        const string colUpdated = "LastUpdated (Local)";

        var invariant = CultureInfo.InvariantCulture;
        string FormatLatLon(double value) => value.ToString("0.0000", invariant);
        string FormatUpdated(DateTimeOffset dt) => dt == default ? string.Empty : dt.ToString("O", invariant);
        string FormatTemp(LocationRow row) => row.LastUpdated == default ? string.Empty : row.TemperatureC.ToString("0.00", invariant) + "°C";

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
            wLat = Math.Max(wLat, FormatLatLon(row.Latitude).Length);
            wLon = Math.Max(wLon, FormatLatLon(row.Longitude).Length);
            wTemp = Math.Max(wTemp, FormatTemp(row).Length);
            wUpdated = Math.Max(wUpdated, FormatUpdated(row.LastUpdated).Length);
        }

        static string PadRight(string? text, int width) => (text ?? string.Empty).PadRight(width);
        static string PadLeft(string? text, int width) => (text ?? string.Empty).PadLeft(width);
        static string Dashes(int width) => new('-', width);

        var sb = new StringBuilder();
        sb.AppendLine($"{PadRight(colLocation, wLocation)} | {PadRight(colName, wName)} | {PadRight(colLat, wLat)} | {PadRight(colLon, wLon)} | {PadRight(colTemp, wTemp)} | {PadRight(colUpdated, wUpdated)}");
        sb.AppendLine($"{Dashes(wLocation)}-|-{Dashes(wName)}-|-{Dashes(wLat)}-|-{Dashes(wLon)}-|-{Dashes(wTemp)}-|-{Dashes(wUpdated)}");

        foreach (var row in rows)
        {
            sb.AppendLine(
                $"{PadRight(row.Location, wLocation)} | {PadRight(row.Name, wName)} | {PadLeft(FormatLatLon(row.Latitude), wLat)} | {PadLeft(FormatLatLon(row.Longitude), wLon)} | {PadLeft(FormatTemp(row), wTemp)} | {PadLeft(FormatUpdated(row.LastUpdated), wUpdated)}");
        }

        return sb.ToString();
    }
}
