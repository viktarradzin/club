namespace WebApi.Services.Caching;

public interface ICardCacheVersionService
{
    Task<long> GetAsync(long userId, CancellationToken ct);

    Task<long> GetGlobalAsync(CancellationToken ct);

    Task IncrementAsync(long userId, CancellationToken ct);
}
