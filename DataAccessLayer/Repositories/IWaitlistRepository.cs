using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<WaitlistEntry?> GetByIdForUserAsync(long id, long userId, CancellationToken ct = default);

    Task<WaitlistEntry?> GetFirstWaitingAsync(long classId, CancellationToken ct = default);

    Task<bool> IsWaitingAsync(
        long userId,
        long classId,
        CancellationToken ct = default);

    Task<int> CountWaitingBeforeAsync(
        long classId,
        DateTime createdAt,
        long entryId,
        CancellationToken ct = default);
}
