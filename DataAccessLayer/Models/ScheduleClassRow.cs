namespace DataAccessLayer.Models;

public class ScheduleClassRow
{
    public long ClassId { get; init; }

    public string Title { get; init; } = null!;

    public DateTime StartsAt { get; init; }

    public int DurationMinutes { get; init; }

    public long RoomId { get; init; }

    public string RoomName { get; init; } = null!;

    public int Capacity { get; init; }

    public long OccupiedSeats { get; init; }

    public long MySeats { get; init; }
}
