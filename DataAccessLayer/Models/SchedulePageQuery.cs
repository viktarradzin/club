namespace DataAccessLayer.Models;

public class SchedulePageQuery
{
    public DateTime From { get; init; }

    public DateTime To { get; init; }

    public long? RoomId { get; init; }

    public long UserId { get; init; }

    public int Take { get; init; }

    public DateTime? AfterStartsAt { get; init; }

    public long? AfterClassId { get; init; }
}
