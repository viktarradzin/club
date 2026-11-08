using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class WaitlistRepository
    : GenericRepository<WaitlistEntry>, IWaitlistRepository
{
    public WaitlistRepository(DbSet<WaitlistEntry> dbSet)
        : base(dbSet)
    {
    }

    public Task<WaitlistEntry?> GetByIdForUserAsync(long id, long userId, CancellationToken ct = default)
    {
        return _dbSet.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
    }

    public Task<WaitlistEntry?> GetFirstWaitingAsync(long classId, CancellationToken ct = default)
    {
        // решение: при одинаковом createdAt упорядочиваем заявки по id,
        // чтобы выдача мест и расчёт позиции использовали один устойчивый порядок.
        return _dbSet
            .Where(x => x.ClassId == classId && x.Status == "waiting")
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
    }

    public Task<bool> IsWaitingAsync(
        long userId,
        long classId,
        CancellationToken ct = default)
    {
        return _dbSet.AnyAsync(
            x => x.UserId == userId
                 && x.ClassId == classId
                 && x.Status == "waiting",
            ct);
    }

    public Task<int> CountWaitingBeforeAsync(
        long classId,
        DateTime createdAt,
        long entryId,
        CancellationToken ct = default)
    {
        return _dbSet.CountAsync(
            x => x.ClassId == classId
                 && x.Status == "waiting"
                 && (x.CreatedAt < createdAt
                     || (x.CreatedAt == createdAt && x.Id < entryId)),
            ct);
    }
}
