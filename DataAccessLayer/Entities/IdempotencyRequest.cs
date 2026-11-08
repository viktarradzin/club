using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class IdempotencyRequest : IEntity
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string Operation { get; set; } = null!;

    public string Key { get; set; } = null!;

    public string KeyHash { get; set; } = null!;

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public string ResponseBody { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
