namespace WebApi.Contracts.Schedule;

public class ScheduleResult
{
    private ScheduleResult(ScheduleResponse? response, string? error)
    {
        Response = response;
        Error = error;
    }

    public ScheduleResponse? Response { get; }

    public string? Error { get; }

    public static ScheduleResult Success(ScheduleResponse response)
    {
        return new ScheduleResult(response, null);
    }

    public static ScheduleResult Failure(string error)
    {
        return new ScheduleResult(null, error);
    }
}
