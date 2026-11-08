using WebApi.Contracts.Schedule;

namespace WebApi.Services.Schedule;

public static class ScheduleQueryValidator
{
    public static bool TryValidatePeriod(
        ScheduleQuery query,
        out DateTime from,
        out DateTime to,
        out string? error)
    {
        from = default;
        to = default;
        error = null;

        if (!ScheduleDateParser.TryParseUtc(query.From, out from))
        {
            error = "Invalid from date";
            return false;
        }

        if (!ScheduleDateParser.TryParseUtc(query.To, out to))
        {
            error = "Invalid to date";
            return false;
        }

        if (to <= from)
        {
            error = "The end of the period must be later than the start";
            return false;
        }

        return true;
    }
}
