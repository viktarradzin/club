using System.Globalization;

namespace WebApi.Services.Schedule;

public static class ScheduleDateParser
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm"
    ];

    public static bool TryParseUtc(string? value, out DateTime result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.EndsWith('Z'))
        {
            value = value[..^1] + "+00:00";
        }

        var styles =
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal;

        if (!DateTimeOffset.TryParseExact(
                value,
                Formats,
                CultureInfo.InvariantCulture,
                styles,
                out var parsed))
        {
            return false;
        }

        result = parsed.UtcDateTime;
        return true;
    }
}
