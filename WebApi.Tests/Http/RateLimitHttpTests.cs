using WebApi.Contracts.Enrollments.Create;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Common.RateLimiting;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class RateLimitHttpTests
{
    private const string MissingEnrollmentUrl = "/enrollments/9223372036854775807";
    private const string ScheduleUrl = "/schedule?from=2099-01-01T00:00:00Z&to=2099-01-02T00:00:00Z";
    private const string AnalyticsUrl = "/analytics/load?from=2099-01-01&to=2099-01-02";

    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private User _admin = null!;
    private User _other = null!;
    private Class _class = null!;
    private string _token = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        _admin = NewUser("admin");
        _other = NewUser("user");
        _class = new Class
        {
            Title = "Rate limit test",
            Room = new Room { Name = "Rate limit room" },
            StartsAt = DateTime.UtcNow.AddDays(3),
            DurationMinutes = 60,
            Capacity = 100
        };

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        db.Users.AddRange(_admin, _other);
        db.Classes.Add(_class);
        await db.SaveChangesAsync();
        db.Passes.Add(new Pass
        {
            UserId = _admin.Id,
            RemainingVisits = 100,
            ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
        });
        await db.SaveChangesAsync();
        _token = CreateToken(_admin);
    }

    [TestCleanup]
    public void CleanUp()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task AllEightEndpoints_WhenBudgetIsExhausted_ReturnProblemDetailsWithRetryAfter()
    {
        var calls = new[]
        {
            new ApiCall(HttpMethod.Post, "/enrollments", HttpStatusCode.NotFound,
                new { classId = long.MaxValue, seats = 1 }, "missing-class"),
            new ApiCall(HttpMethod.Post, MissingEnrollmentUrl + "/seats", HttpStatusCode.NotFound,
                new { seats = 1 }),
            new ApiCall(HttpMethod.Post, MissingEnrollmentUrl + "/cancel", HttpStatusCode.NotFound),
            new ApiCall(HttpMethod.Get, MissingEnrollmentUrl, HttpStatusCode.NotFound),
            new ApiCall(HttpMethod.Get, ScheduleUrl, HttpStatusCode.OK),
            new ApiCall(HttpMethod.Get, AnalyticsUrl, HttpStatusCode.OK),
            new ApiCall(HttpMethod.Post, "/enrollments/waitlist", HttpStatusCode.NotFound,
                new { classId = long.MaxValue, seats = 1 }, "missing-waitlist-class"),
            new ApiCall(HttpMethod.Post, "/enrollments/waitlist/9223372036854775807/cancel", HttpStatusCode.NotFound)
        };

        for (var i = 0; i < 60; i++)
        {
            var call = calls[i % calls.Length];
            using var response = await SendAsync(_client, _token, call);
            Assert.AreEqual(call.ExpectedStatus, response.StatusCode,
                $"Request {i + 1}: {call.Url}; {await response.Content.ReadAsStringAsync()}");
        }

        foreach (var call in calls)
        {
            using var response = await SendAsync(_client, _token, call);
            await AssertRateLimitedAsync(response);
        }
    }

    [TestMethod]
    public async Task Limit_IsSharedAcrossTokensAndApiInstances_ButSeparateForEachUser()
    {
        await ExhaustBudgetAsync(_client, _token);
        var renewedToken = CreateToken(new User
        {
            Id = _admin.Id, Role = _admin.Role, Email = "changed-claim@example.com"
        });
        Assert.AreNotEqual(_token, renewedToken);

        using var anotherFactory = new ApiApplicationFactory();
        using var anotherClient = anotherFactory.CreateClient();
        using var sameUser = await SendAsync(anotherClient, renewedToken,
            new ApiCall(HttpMethod.Get, MissingEnrollmentUrl, HttpStatusCode.TooManyRequests));
        await AssertRateLimitedAsync(sameUser);

        using var otherUser = await SendAsync(anotherClient, CreateToken(_other),
            new ApiCall(HttpMethod.Get, MissingEnrollmentUrl, HttpStatusCode.NotFound));
        Assert.AreEqual(HttpStatusCode.NotFound, otherUser.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentRequests_WhenSentTogether_AllowExactlySixty()
    {
        var call = new ApiCall(HttpMethod.Get, MissingEnrollmentUrl, HttpStatusCode.NotFound);
        var statuses = await Task.WhenAll(Enumerable.Range(0, 80).Select(async _ =>
        {
            using var response = await SendAsync(_client, _token, call);
            return response.StatusCode;
        }));

        Assert.AreEqual(60, statuses.Count(x => x == HttpStatusCode.NotFound));
        Assert.AreEqual(20, statuses.Count(x => x == HttpStatusCode.TooManyRequests));
    }

    [TestMethod]
    public async Task IdempotentReplays_CountTowardLimit_AndRejectedWritesDoNotChangeData()
    {
        var create = new ApiCall(HttpMethod.Post, "/enrollments", HttpStatusCode.Created,
            new { classId = _class.Id, seats = 1 }, "same-booking");
        using var first = await SendAsync(_client, _token, create);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        var firstBody = await first.Content.ReadAsStringAsync();
        var enrollment = await first.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        Assert.IsNotNull(enrollment);

        for (var i = 1; i < 60; i++)
        {
            using var replay = await SendAsync(_client, _token, create);
            Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode, $"Request {i + 1}");
            Assert.AreEqual(firstBody, await replay.Content.ReadAsStringAsync());
        }

        var rejectedCalls = new[]
        {
            create with { Key = "new-booking" },
            new ApiCall(HttpMethod.Post, $"/enrollments/{enrollment.Id}/cancel", HttpStatusCode.TooManyRequests),
            new ApiCall(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats", HttpStatusCode.TooManyRequests,
                new { seats = 2 }),
            new ApiCall(HttpMethod.Post, "/enrollments/waitlist", HttpStatusCode.TooManyRequests,
                new { classId = _class.Id, seats = 100 }, "new-waitlist")
        };
        foreach (var call in rejectedCalls)
        {
            using var response = await SendAsync(_client, _token, call);
            await AssertRateLimitedAsync(response);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var saved = await db.Enrollments.Where(x => x.UserId == _admin.Id).ToListAsync();
        Assert.AreEqual(1, saved.Count);
        Assert.AreEqual(enrollment.Id, saved[0].Id);
        Assert.AreEqual("active", saved[0].Status);
        Assert.AreEqual(1, saved[0].Seats);
        Assert.AreEqual(99, (await db.Passes.SingleAsync(x => x.UserId == _admin.Id)).RemainingVisits);
        Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => x.UserId == _admin.Id));
        Assert.AreEqual(0, await db.WaitlistEntries.CountAsync(x => x.UserId == _admin.Id));
    }

    [TestMethod]
    public async Task ValidationAndBusinessErrors_WhenReturned_CountTowardLimit()
    {
        var invalid = new ApiCall(HttpMethod.Post, "/enrollments", HttpStatusCode.BadRequest, new { });
        var conflict = new ApiCall(HttpMethod.Post, "/enrollments/waitlist", HttpStatusCode.Conflict,
            new { classId = _class.Id, seats = 1 }, "seats-available");
        for (var i = 0; i < 60; i++)
        {
            var call = i % 2 == 0 ? invalid : conflict;
            using var response = await SendAsync(_client, _token, call);
            Assert.AreEqual(call.ExpectedStatus, response.StatusCode, $"Request {i + 1}");
        }

        using var rejected = await SendAsync(_client, _token,
            new ApiCall(HttpMethod.Post, "/enrollments", HttpStatusCode.TooManyRequests,
                new { classId = _class.Id, seats = 1 }, "after-errors"));
        await AssertRateLimitedAsync(rejected);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(0, await db.Enrollments.CountAsync(x => x.UserId == _admin.Id));
        Assert.AreEqual(0, await db.WaitlistEntries.CountAsync(x => x.UserId == _admin.Id));
        Assert.AreEqual(0, await db.IdempotencyRequests.CountAsync(x => x.UserId == _admin.Id));
        Assert.AreEqual(100, (await db.Passes.SingleAsync(x => x.UserId == _admin.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task InfrastructureRoutes_DoNotConsumeBudget_AndRemainAvailableWhenExhausted()
    {
        var excluded = new[]
        {
            new ApiCall(HttpMethod.Get, "/health", HttpStatusCode.OK),
            new ApiCall(HttpMethod.Get, "/swagger/index.html", HttpStatusCode.OK),
            new ApiCall(HttpMethod.Post, "/auth/login", HttpStatusCode.Unauthorized,
                new { email = "missing-rate-limit-test@example.com", password = "invalid" }),
            new ApiCall(HttpMethod.Get, "/enrollments/no-such/route", HttpStatusCode.NotFound)
        };

        foreach (var call in excluded)
        {
            using var response = await SendAsync(_client, _token, call);
            Assert.AreEqual(call.ExpectedStatus, response.StatusCode, call.Url);
        }
        await ExhaustBudgetAsync(_client, _token);
        foreach (var call in excluded)
        {
            using var response = await SendAsync(_client, _token, call);
            Assert.AreEqual(call.ExpectedStatus, response.StatusCode, call.Url);
        }
    }

    [TestMethod]
    public async Task Authentication_PrecedesRateLimit_AndForbiddenRequestsConsumeBudget()
    {
        var enrollment = new ApiCall(HttpMethod.Get, MissingEnrollmentUrl, HttpStatusCode.Unauthorized);
        using var anonymous = await SendAsync(_client, null, enrollment);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var invalidToken = await SendAsync(_client, "invalid-token", enrollment);
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalidToken.StatusCode);

        var otherToken = CreateToken(_other);
        var analytics = new ApiCall(HttpMethod.Get, AnalyticsUrl, HttpStatusCode.Forbidden);
        for (var i = 0; i < 60; i++)
        {
            using var forbidden = await SendAsync(_client, otherToken, analytics);
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode, $"Request {i + 1}");
        }

        using var limitedAnalytics = await SendAsync(_client, otherToken, analytics);
        await AssertRateLimitedAsync(limitedAnalytics);
        using var rateLimited = await SendAsync(_client, otherToken, enrollment);
        await AssertRateLimitedAsync(rateLimited);
    }

    [TestMethod]
    public async Task RedisFailure_WhenLimiterFails_DoesNotRunEnrollmentOperation()
    {
        var limiter = new Mock<IUserRateLimiter>();
        limiter.Setup(x => x.TryAcquireAsync(_admin.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Test failure"));
        using var failingFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserRateLimiter>();
            services.AddSingleton(limiter.Object);
        }));
        using var client = failingFactory.CreateClient();
        using var response = await SendAsync(client, _token,
            new ApiCall(HttpMethod.Post, "/enrollments", HttpStatusCode.InternalServerError,
                new { classId = _class.Id, seats = 1 }, "redis-unavailable"));
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(500, body.RootElement.GetProperty("status").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(0, await db.Enrollments.CountAsync(x => x.UserId == _admin.Id));
        Assert.AreEqual(0, await db.IdempotencyRequests.CountAsync(x => x.UserId == _admin.Id));
        Assert.AreEqual(100, (await db.Passes.SingleAsync(x => x.UserId == _admin.Id)).RemainingVisits);
    }

    private static async Task ExhaustBudgetAsync(HttpClient client, string token)
    {
        for (var i = 0; i < 60; i++)
        {
            using var response = await SendAsync(client, token,
                new ApiCall(HttpMethod.Get, MissingEnrollmentUrl, HttpStatusCode.NotFound));
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"Request {i + 1}");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? token, ApiCall call)
    {
        using var request = new HttpRequestMessage(call.Method, call.Url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (call.Key is not null) request.Headers.Add("Idempotency-Key", call.Key);
        if (call.Body is not null) request.Content = JsonContent.Create(call.Body);
        return await client.SendAsync(request);
    }

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsNotNull(response.Headers.RetryAfter?.Delta);
        Assert.IsTrue(response.Headers.RetryAfter.Delta >= TimeSpan.FromSeconds(1));
        Assert.IsTrue(response.Headers.RetryAfter.Delta <= TimeSpan.FromSeconds(60));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(429, body.RootElement.GetProperty("status").GetInt32());
    }

    private string CreateToken(User user) => _factory.Services.GetRequiredService<AccessTokenService>()
        .Create(user).AccessToken;

    private static User NewUser(string role) => new()
    {
        Email = $"rate-limit-{Guid.NewGuid():N}@example.com",
        PasswordHash = "unused",
        Role = role,
        CreatedAt = DateTime.UtcNow
    };

    private sealed record ApiCall(HttpMethod Method, string Url, HttpStatusCode ExpectedStatus,
        object? Body = null, string? Key = null);
}
