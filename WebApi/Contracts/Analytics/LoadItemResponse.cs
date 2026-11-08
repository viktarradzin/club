namespace WebApi.Contracts.Analytics;

public class LoadItemResponse
{
    public DateOnly Date { get; init; }

    public LoadRoomResponse Room { get; init; } = null!;

    public int ClassesCount { get; init; }

    public int TotalCapacity { get; init; }

    public int BookedSeats { get; init; }
}
