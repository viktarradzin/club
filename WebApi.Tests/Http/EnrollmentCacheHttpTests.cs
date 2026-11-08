using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Common.Caching;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Contracts.Enrollments;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class EnrollmentCacheHttpTests
{
    [TestMethod]
    public async Task GetCard_WhenRequested_StoresVersionedEntryInRedis()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        var (user, enrollmentId) = await CreateDataAsync(factory);
        Authenticate(factory, client, user);

        long version;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            version = await db.EnrollmentCardCacheVersions.Where(x => x.Id == user.Id).Select(x => x.Version).SingleOrDefaultAsync();
        }
        var redis = factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var cacheKey = $"enrollment-card:v2:{version}:{user.Id}:{enrollmentId}";
        Assert.IsFalse(await redis.KeyExistsAsync(cacheKey));

        using var response = await client.GetAsync($"/enrollments/{enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(await redis.KeyExistsAsync(cacheKey));
        var ttl = await redis.KeyTimeToLiveAsync(cacheKey);
        Assert.IsTrue(ttl.HasValue && ttl.Value > TimeSpan.Zero);
    }

    [TestMethod]
    public async Task GetCard_WhenOldCacheWriteFinishesAfterChange_NextReadIsCurrent()
    {
        using var baseFactory = new ApiApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICacheService>();
                services.AddSingleton<DelayedCache>();
                services.AddSingleton<ICacheService>(provider => provider.GetRequiredService<DelayedCache>());
            }));
        using var client = factory.CreateClient();
        var (user, enrollmentId) = await CreateDataAsync(factory);
        Authenticate(factory, client, user);
        var cache = factory.Services.GetRequiredService<DelayedCache>();
        cache.DelayNextWrite();

        var oldRead = client.GetAsync($"/enrollments/{enrollmentId}");
        try
        {
            await cache.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));

            using var change = await client.PostAsJsonAsync($"/enrollments/{enrollmentId}/seats", new { seats = 3 });
            Assert.AreEqual(HttpStatusCode.OK, change.StatusCode);
        }
        finally
        {
            cache.ReleaseWrite();
        }

        using var oldResponse = await oldRead.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(HttpStatusCode.OK, oldResponse.StatusCode);
        var oldCard = await oldResponse.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(oldCard);
        Assert.AreEqual(2, oldCard.Seats);

        using var currentResponse = await client.GetAsync($"/enrollments/{enrollmentId}");
        Assert.AreEqual(HttpStatusCode.OK, currentResponse.StatusCode);
        var currentCard = await currentResponse.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(currentCard);
        Assert.AreEqual(3, currentCard.Seats);
        Assert.AreEqual(4, currentCard.Pass.RemainingVisits);
    }

    private static async Task<(User User, long EnrollmentId)> CreateDataAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var user = new User
        {
            Email = $"cache-{Guid.NewGuid():N}@test.local", PasswordHash = "test",
            Role = "user", CreatedAt = DateTime.UtcNow
        };
        var classEntity = new Class
        {
            Title = "Cache test", Room = new Room { Name = "Cache room" },
            StartsAt = DateTime.UtcNow.AddHours(4), DurationMinutes = 60, Capacity = 10
        };
        db.Users.Add(user);
        db.Classes.Add(classEntity);
        await db.SaveChangesAsync();
        db.Passes.Add(new Pass
        {
            UserId = user.Id, RemainingVisits = 5,
            ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
        });
        var enrollment = new Enrollment
        {
            UserId = user.Id, ClassId = classEntity.Id, Seats = 2,
            Status = "active", CreatedAt = DateTime.UtcNow
        };
        db.Enrollments.Add(enrollment);
        await db.SaveChangesAsync();
        return (user, enrollment.Id);
    }

    private static void Authenticate(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        HttpClient client,
        User user)
    {
        var token = factory.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed class DelayedCache : ICacheService
    {
        private readonly RedisCacheService _inner;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delayNext;

        public DelayedCache(IConnectionMultiplexer connection)
        {
            _inner = new RedisCacheService(connection, TimeSpan.FromMinutes(5));
        }

        public Task WriteStarted => _started.Task;

        public void DelayNextWrite() => Interlocked.Exchange(ref _delayNext, 1);

        public void ReleaseWrite() => _release.TrySetResult();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) => _inner.GetAsync<T>(key, ct);

        public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _delayNext, 0) == 1)
            {
                _started.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            await _inner.SetAsync(key, value, ttl, ct);
        }

        public Task RemoveAsync(string key, CancellationToken ct = default) => _inner.RemoveAsync(key, ct);
    }
}
