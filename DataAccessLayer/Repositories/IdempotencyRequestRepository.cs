using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public sealed class IdempotencyRequestRepository : GenericRepository<IdempotencyRequest>, IIdempotencyRequestRepository
{
    public IdempotencyRequestRepository(DbSet<IdempotencyRequest> dbSet)
        : base(dbSet)
    {
    }

    public Task<IdempotencyRequest?> GetByKeyAsync(
        long userId,
        string operation,
        string keyHash,
        string key,
        CancellationToken ct = default)
    {
        return _dbSet.AsNoTracking().SingleOrDefaultAsync(
            x => x.UserId == userId && x.Operation == operation && x.KeyHash == keyHash && x.Key == key,
            ct);
    }
}
