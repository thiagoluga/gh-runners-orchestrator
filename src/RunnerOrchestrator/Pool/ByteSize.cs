using System.Globalization;

namespace RunnerOrchestrator.Pool;

/// <summary>Parses Docker-style sizes: <c>512m</c>, <c>16g</c>, or a byte count.</summary>
internal static class ByteSize
{
    public static bool TryParse(string? value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().ToLowerInvariant();
        var multiplier = text[^1] switch
        {
            'k' => 1024L,
            'm' => 1024L * 1024,
            'g' => 1024L * 1024 * 1024,
            _ => 1L,
        };
        var number = multiplier == 1 ? text : text[..^1];
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return false;
        }

        bytes = checked(parsed * multiplier);
        return true;
    }
}
