using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Auth;
using WebApi.Contracts.Enrollments.Create;
using WebApi.Services.Caching;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class EnrollmentConcurrencyHttpTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void SetUp()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void CleanUp()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [DataTestMethod]
    [DataRow(20)]
    [DataRow(40)]
    public async Task Create_IndependentUsersAndClasses_AllReturnCreated(int count)
    {
        var scenarios = new List<(User User, Class Class)>();
        for (var i = 0; i < count; i++) scenarios.Add(await NewScenarioAsync());

        var responses = await SendTogetherAsync(scenarios.Select((scenario, i) =>
            (Func<Task<HttpResponseMessage>>)(() => CreateAsync(scenario.User, scenario.Class.Id, $"independent-{i}"))));
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.Created), Statuses(responses));
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var ids = scenarios.Select(x => x.User.Id).ToArray();
            Assert.AreEqual(count, await db.Enrollments.CountAsync(x => ids.Contains(x.UserId)));
            Assert.IsTrue(await db.Passes.Where(x => ids.Contains(x.UserId)).AllAsync(x => x.RemainingVisits == 9));
            Assert.AreEqual(count, await db.IdempotencyRequests.CountAsync(x => ids.Contains(x.UserId)));
            Assert.AreEqual(count, await db.EnrollmentCardCacheVersions.CountAsync(x => ids.Contains(x.Id) && x.Version == 1));
        }
        finally { Dispose(responses); }
    }

    [TestMethod]
    public async Task Create_SameKeyInParallel_ReturnsSameBodyAndDebitsOnce()
    {
        var (user, lesson) = await NewScenarioAsync();
        var responses = await SendTogetherAsync(Enumerable.Range(0, 20).Select(_ =>
            (Func<Task<HttpResponseMessage>>)(() => CreateAsync(user, lesson.Id, "same-key", 2))));
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.Created), Statuses(responses));
            var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync()));
            Assert.AreEqual(1, bodies.Distinct().Count());
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            Assert.AreEqual(1, await db.Enrollments.CountAsync(x => x.UserId == user.Id));
            Assert.AreEqual(8, (await db.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits);
            Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => x.UserId == user.Id));
            Assert.AreEqual(1L, (await db.EnrollmentCardCacheVersions.SingleAsync(x => x.Id == user.Id)).Version);
        }
        finally { Dispose(responses); }
    }

    [TestMethod]
    public async Task Create_LastSeatInParallel_CreatesOnlyOneEnrollment()
    {
        var (firstUser, lesson) = await NewScenarioAsync(capacity: 1);
        var users = new List<User> { firstUser };
        for (var i = 1; i < 20; i++) users.Add((await NewScenarioAsync()).User);

        var responses = await SendTogetherAsync(users.Select((user, i) =>
            (Func<Task<HttpResponseMessage>>)(() => CreateAsync(user, lesson.Id, $"last-seat-{i}"))));
        try
        {
            Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created), Statuses(responses));
            foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
                await AssertConflictAsync(response, "capacity-exceeded");
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            Assert.AreEqual(1L, await db.Enrollments.Where(x => x.ClassId == lesson.Id && x.Status == "active")
                .SumAsync(x => (long)x.Seats));
            var userIds = users.Select(x => x.Id).ToArray();
            Assert.AreEqual(users.Count * 10 - 1, await db.Passes.Where(x => userIds.Contains(x.UserId))
                .SumAsync(x => x.RemainingVisits));
            Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => userIds.Contains(x.UserId)));
        }
        finally { Dispose(responses); }
    }

    [TestMethod]
    public async Task Create_SamePassForDifferentClasses_NeverOverdrawsVisits()
    {
        var (user, firstClass) = await NewScenarioAsync(visits: 3);
        var classes = new List<Class> { firstClass };
        for (var i = 1; i < 20; i++) classes.Add((await NewScenarioAsync()).Class);

        var responses = await SendTogetherAsync(classes.Select((lesson, i) =>
            (Func<Task<HttpResponseMessage>>)(() => CreateAsync(user, lesson.Id, $"same-pass-{i}"))));
        try
        {
            Assert.AreEqual(3, responses.Count(x => x.StatusCode == HttpStatusCode.Created), Statuses(responses));
            foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
                await AssertConflictAsync(response, "pass-exhausted");
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            Assert.AreEqual(0, (await db.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits);
            Assert.AreEqual(3, await db.Enrollments.CountAsync(x => x.UserId == user.Id));
            Assert.AreEqual(3, await db.IdempotencyRequests.CountAsync(x => x.UserId == user.Id));
            Assert.AreEqual(3L, (await db.EnrollmentCardCacheVersions.SingleAsync(x => x.Id == user.Id)).Version);
        }
        finally { Dispose(responses); }
    }

    [TestMethod]
    public async Task Create_WhenVersionUpdateFails_RollsBackBookingAndCanRetrySameKey()
    {
        var (user, lesson) = await NewScenarioAsync();
        using var failingFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICardCacheVersionService>();
            services.AddScoped<ICardCacheVersionService, FailingVersion>();
        }));
        using var client = failingFactory.CreateClient();
        using var failure = await CreateAsync(user, lesson.Id, "rollback", 2, client);
        Assert.AreEqual(HttpStatusCode.InternalServerError, failure.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            Assert.AreEqual(0, await db.Enrollments.CountAsync(x => x.UserId == user.Id));
            Assert.AreEqual(10, (await db.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits);
            Assert.AreEqual(0, await db.IdempotencyRequests.CountAsync(x => x.UserId == user.Id));
            Assert.IsFalse(await db.EnrollmentCardCacheVersions.AnyAsync(x => x.Id == user.Id));
        }
        using var retry = await CreateAsync(user, lesson.Id, "rollback", 2);
        Assert.AreEqual(HttpStatusCode.Created, retry.StatusCode, await retry.Content.ReadAsStringAsync());
        var created = await retry.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        Assert.IsNotNull(created);
        using var checkScope = _factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(1, await check.Enrollments.CountAsync(x => x.UserId == user.Id));
        Assert.AreEqual(8, (await check.Passes.SingleAsync(x => x.UserId == user.Id)).RemainingVisits);
        Assert.AreEqual(1L, (await check.EnrollmentCardCacheVersions.SingleAsync(x => x.Id == user.Id)).Version);
    }

    private async Task<(User User, Class Class)> NewScenarioAsync(int visits = 10, int capacity = 10)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var user = new User
        {
            Email = $"concurrent-{Guid.NewGuid():N}@test.local", PasswordHash = "unused",
            Role = "user", CreatedAt = DateTime.UtcNow
        };
        var lesson = new Class
        {
            Title = "Concurrent booking", Room = new Room { Name = "Concurrent room" },
            StartsAt = DateTime.UtcNow.AddDays(3), DurationMinutes = 60, Capacity = capacity
        };
        db.AddRange(user, lesson);
        await db.SaveChangesAsync();
        db.Passes.Add(new Pass
        {
            UserId = user.Id, RemainingVisits = visits,
            ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
        });
        await db.SaveChangesAsync();
        return (user, lesson);
    }

    private async Task<HttpResponseMessage> CreateAsync(User user, long classId, string key, int seats = 1, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken);
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(new { classId, seats });
        return await (client ?? _client).SendAsync(request);
    }

    private static async Task<HttpResponseMessage[]> SendTogetherAsync(IEnumerable<Func<Task<HttpResponseMessage>>> calls)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(async call =>
        {
            await start.Task;
            return await call();
        }).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string reason)
    {
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
    }

    private static string Statuses(IEnumerable<HttpResponseMessage> responses) => string.Join(", ",
        responses.GroupBy(x => x.StatusCode).Select(x => $"{(int)x.Key}: {x.Count()}"));

    private static void Dispose(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses) response.Dispose();
    }

    private sealed class FailingVersion : ICardCacheVersionService
    {
        public Task<long> GetAsync(long userId, CancellationToken ct) => Task.FromResult(0L);
        public Task<long> GetGlobalAsync(CancellationToken ct) => Task.FromResult(0L);
        public Task IncrementAsync(long userId, CancellationToken ct) => throw new InvalidOperationException("Version update failure");
    }
}
