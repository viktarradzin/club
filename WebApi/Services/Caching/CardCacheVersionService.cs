using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Services.Caching;

public sealed class CardCacheVersionService : ICardCacheVersionService
{
    private readonly IUnitOfWork _unitOfWork;

    public CardCacheVersionService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<long> GetAsync(long userId, CancellationToken ct)
    {
        return await _unitOfWork.DbContext
            .Set<EnrollmentCardCacheVersion>()
            .Where(x => x.Id == userId)
            .Select(x => x.Version)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<long> GetGlobalAsync(CancellationToken ct)
    {
        return await _unitOfWork.DbContext
            .Set<EnrollmentCardCacheVersion>()
            .Select(x => x.Version)
            .Concat(_unitOfWork.DbContext.Set<ScheduleCacheVersion>().Select(x => x.Version))
            .SumAsync(ct);
    }

    public async Task IncrementAsync(long userId, CancellationToken ct)
    {
        await _unitOfWork.DbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO card_cache_version (id, version) VALUES ({userId}, 1)
            ON CONFLICT (id) DO UPDATE SET version = card_cache_version.version + 1
            """, ct);
    }
}
