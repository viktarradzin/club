namespace Common.RateLimiting;

public interface IUserRateLimiter
{
    Task<RateLimitDecision> TryAcquireAsync(long userId, CancellationToken ct = default);
}
