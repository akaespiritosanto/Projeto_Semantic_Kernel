namespace semantic_kernel;

public static class DebugUtil
{
    public static bool IsEnabled()
    {
#if DEBUG
        return true;
#else
        var value = Environment.GetEnvironmentVariable("APP_DEBUG");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
#endif
    }

    public static void Log(string message)
    {
        if (!IsEnabled())
        {
            return;
        }

        Console.Error.WriteLine($"[debug {DateTime.UtcNow:O}] {message}");
    }

    public static string Truncate(string? text, int maxChars = 200)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        return text.Substring(0, maxChars) + "...";
    }
}

