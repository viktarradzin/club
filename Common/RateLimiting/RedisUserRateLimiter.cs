using System.Globalization;
using StackExchange.Redis;

namespace Common.RateLimiting;

public sealed class RedisUserRateLimiter : IUserRateLimiter
{
    private const int RequestLimit = 60;
    private const int WindowMilliseconds = 60_000;

    private const string AcquireScript = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local window = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])

        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        if redis.call('ZCARD', KEYS[1]) >= limit then
            local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
            local retryAfter = math.max(1, math.ceil((tonumber(oldest[2]) + window - now) / 1000))
            return {0, retryAfter}
        end

        redis.call('ZADD', KEYS[1], now, ARGV[3])
        redis.call('PEXPIRE', KEYS[1], window)
        return {1, 0}
        """;

    private readonly IConnectionMultiplexer _connection;

    public RedisUserRateLimiter(IConnectionMultiplexer connection)
    {
        _connection = connection;
    }

    public async Task<RateLimitDecision> TryAcquireAsync(long userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = "rate-limit:club-api:v1:user:" + userId.ToString(CultureInfo.InvariantCulture);
        var result = await _connection.GetDatabase().ScriptEvaluateAsync(
            AcquireScript,
            [key],
            [WindowMilliseconds, RequestLimit, Guid.NewGuid().ToString("N")])
            .WaitAsync(ct);

        var values = (RedisResult[])result!;
        return new RateLimitDecision((int)values[0] == 1, (int)values[1]);
    }
}
