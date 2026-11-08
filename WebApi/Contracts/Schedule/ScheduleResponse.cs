namespace WebApi.Contracts.Schedule;

public class ScheduleResponse
{
    public IReadOnlyList<ScheduleItemResponse> Items { get; init; } = [];

    public string? ContinuationToken { get; init; }
}
