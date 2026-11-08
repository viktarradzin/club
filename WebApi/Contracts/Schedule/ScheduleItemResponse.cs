namespace WebApi.Contracts.Schedule;

public class ScheduleItemResponse
{
    public long ClassId { get; init; }

    public string Title { get; init; } = null!;

    public DateTime StartsAt { get; init; }

    public int DurationMinutes { get; init; }

    public ScheduleRoomResponse Room { get; init; } = null!;

    public int Capacity { get; init; }

    public int FreeSeats { get; init; }

    public int MySeats { get; init; }
}
