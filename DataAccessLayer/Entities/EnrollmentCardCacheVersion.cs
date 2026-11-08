using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class EnrollmentCardCacheVersion : IEntity
{
    public long Id { get; set; }
    public long Version { get; set; }

}
