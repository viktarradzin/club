namespace DataAccessLayer.Models;

public class RoomLoadRow
{
    public DateOnly Date { get; init; }

    public long RoomId { get; init; }

    public string RoomName { get; init; } = null!;

    public int ClassesCount { get; init; }

    public long TotalCapacity { get; init; }

    public long BookedSeats { get; init; }
}
