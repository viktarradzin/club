using Common.RateLimiting;
using StackExchange.Redis;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Services;

[TestClass]
public class RedisUserRateLimiterTests
{
    private IConnectionMultiplexer _connection = null!;
    private RedisUserRateLimiter _limiter = null!;
    private long _userId;
    private string _key = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(TestDatabase.RedisConnectionString);
        _limiter = new RedisUserRateLimiter(_connection);
        _userId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        _key = $"rate-limit:club-api:v1:user:{_userId}";
    }

    [TestCleanup]
    public async Task CleanUpAsync()
    {
        await _connection.GetDatabase().KeyDeleteAsync(_key);
        _connection.Dispose();
    }

    [TestMethod]
    public async Task TryAcquireAsync_WhenLimitIsReached_RejectsNextWithoutExtendingWindow()
    {
        for (var i = 0; i < 60; i++)
        {
            var allowed = await _limiter.TryAcquireAsync(_userId);
            Assert.IsTrue(allowed.IsAllowed, $"Request {i + 1}");
            Assert.AreEqual(0, allowed.RetryAfterSeconds);
        }

        var db = _connection.GetDatabase();
        var ttlBefore = await db.KeyTimeToLiveAsync(_key);
        var rejected = await _limiter.TryAcquireAsync(_userId);
        var ttlAfter = await db.KeyTimeToLiveAsync(_key);

        Assert.IsFalse(rejected.IsAllowed);
        Assert.IsTrue(rejected.RetryAfterSeconds is >= 1 and <= 60);
        Assert.AreEqual(60L, await db.SortedSetLengthAsync(_key));
        Assert.IsTrue(ttlBefore > TimeSpan.Zero && ttlBefore <= TimeSpan.FromSeconds(60));
        Assert.IsTrue(ttlAfter > TimeSpan.Zero && ttlAfter <= ttlBefore);
    }

    [TestMethod]
    public async Task TryAcquireAsync_WithConcurrentConnections_SharesOneLimit()
    {
        using var otherConnection = await ConnectionMultiplexer.ConnectAsync(TestDatabase.RedisConnectionString);
        var otherLimiter = new RedisUserRateLimiter(otherConnection);

        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(i => (i % 2 == 0 ? _limiter : otherLimiter).TryAcquireAsync(_userId)));

        Assert.AreEqual(60, results.Count(x => x.IsAllowed));
        Assert.AreEqual(40, results.Count(x => !x.IsAllowed));
        Assert.AreEqual(60L, await _connection.GetDatabase().SortedSetLengthAsync(_key));
    }

    [TestMethod]
    public async Task TryAcquireAsync_WhenRequestExpires_ReleasesOnlyOnePlace()
    {
        for (var i = 0; i < 60; i++)
        {
            Assert.IsTrue((await _limiter.TryAcquireAsync(_userId)).IsAllowed);
        }

        await _connection.GetDatabase().ScriptEvaluateAsync("""
            local time = redis.call('TIME')
            local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
            local oldest = redis.call('ZRANGE', KEYS[1], 0, 0)
            redis.call('ZADD', KEYS[1], now - 60000, oldest[1])
            return 1
            """, [_key]);

        Assert.IsTrue((await _limiter.TryAcquireAsync(_userId)).IsAllowed);
        Assert.IsFalse((await _limiter.TryAcquireAsync(_userId)).IsAllowed);
        Assert.AreEqual(60L, await _connection.GetDatabase().SortedSetLengthAsync(_key));
    }

    [TestMethod]
    public async Task TryAcquireAsync_WithDifferentUsers_UsesSeparateLimits()
    {
        var otherId = _userId - 1;
        try
        {
            for (var i = 0; i < 60; i++) await _limiter.TryAcquireAsync(_userId);

            Assert.IsFalse((await _limiter.TryAcquireAsync(_userId)).IsAllowed);
            Assert.IsTrue((await _limiter.TryAcquireAsync(otherId)).IsAllowed);
        }
        finally
        {
            await _connection.GetDatabase().KeyDeleteAsync($"rate-limit:club-api:v1:user:{otherId}");
        }
    }

    [TestMethod]
    public async Task TryAcquireAsync_WithCancelledRequest_DoesNotReservePlace()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => _limiter.TryAcquireAsync(_userId, cancellation.Token));

        Assert.IsFalse(await _connection.GetDatabase().KeyExistsAsync(_key));
    }
}
