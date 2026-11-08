using System.Globalization;
using WebApi.Contracts.Analytics;

namespace WebApi.Services.Analytics;

public static class LoadQueryValidator
{
    public static bool TryValidatePeriod(
        LoadQuery query,
        out DateOnly from,
        out DateOnly to,
        out string? error)
    {
        from = default;
        to = default;
        error = null;

        if (!DateOnly.TryParseExact(
                query.From,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out from))
        {
            error = "Invalid from date";
            return false;
        }

        if (!DateOnly.TryParseExact(
                query.To,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out to))
        {
            error = "Invalid to date";
            return false;
        }

        if (to < from)
        {
            error = "The end of the period cannot be earlier than the start";
            return false;
        }

        return true;
    }
}
