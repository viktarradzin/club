namespace WebApi.Contracts.Schedule;

public class ScheduleContinuation
{
    public int Version { get; init; }

    public DateTime From { get; init; }

    public DateTime To { get; init; }

    public long? RoomId { get; init; }

    public DateTime LastStartsAt { get; init; }

    public long LastClassId { get; init; }
}
