using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IIdempotencyRequestRepository : IGenericRepository<IdempotencyRequest>
{
    Task<IdempotencyRequest?> GetByKeyAsync(
        long userId,
        string operation,
        string keyHash,
        string key,
        CancellationToken ct = default);
}
