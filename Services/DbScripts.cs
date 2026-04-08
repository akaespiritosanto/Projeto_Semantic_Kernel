using System.Collections.Concurrent;

namespace semantic_kernel.Services;

internal static class DbScripts
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Load(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Script path must be provided.", nameof(relativePath));
        }

        return Cache.GetOrAdd(relativePath, static key =>
        {
            var normalized = key.Replace('\\', '/').TrimStart('/');
            var fullPath = Path.Combine(AppContext.BaseDirectory, "DbScripts", normalized);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"DB script not found: '{normalized}' (expected at '{fullPath}').");
            }

            return File.ReadAllText(fullPath);
        });
    }
}

