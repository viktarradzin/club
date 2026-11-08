using WebApi.Contracts.Enrollments.Waitlist;
using WebApi.Services.Waitlist;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Auth;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class CancelWaitlistHttpTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private User _owner = null!;
    private User _holder = null!;
    private User _next = null!;
    private Class _class = null!;
    private Enrollment _enrollment = null!;
    private WaitlistEntry _entry = null!;
    private WaitlistEntry _nextEntry = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        _owner = NewUser();
        _holder = NewUser();
        _next = NewUser();
        var now = DateTime.UtcNow;
        _class = new Class
        {
            Title = "Waitlist cancellation", Room = new Room { Name = "Waitlist cancellation room" },
            StartsAt = now.AddDays(3), DurationMinutes = 60, Capacity = 10
        };

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        db.Users.AddRange(_owner, _holder, _next);
        db.Classes.Add(_class);
        await db.SaveChangesAsync();

        db.Passes.AddRange(new[] { _owner, _holder, _next }.Select(user => new Pass
        {
            UserId = user.Id, RemainingVisits = 20,
            ValidUntil = DateOnly.FromDateTime(now).AddDays(30)
        }));
        _enrollment = new Enrollment
        {
            UserId = _holder.Id, ClassId = _class.Id, Seats = 10, Status = "active", CreatedAt = now
        };
        _entry = new WaitlistEntry
        {
            UserId = _owner.Id, ClassId = _class.Id, Seats = 2, Status = "waiting", CreatedAt = now.AddMinutes(-2)
        };
        _nextEntry = new WaitlistEntry
        {
            UserId = _next.Id, ClassId = _class.Id, Seats = 1, Status = "waiting", CreatedAt = now.AddMinutes(-1)
        };
        db.Enrollments.Add(_enrollment);
        db.WaitlistEntries.AddRange(_entry, _nextEntry);
        await db.SaveChangesAsync();
    }

    [TestCleanup]
    public void CleanUp()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task Cancel_RepeatedRequest_ReturnsSameContractWithoutAffectingBookings()
    {
        using var first = await CancelWaitlistAsync(_owner, _entry.Id);
        using var repeated = await CancelWaitlistAsync(_owner, _entry.Id);
        var firstBody = await AssertCancelledAsync(first);
        Assert.AreEqual(firstBody, await AssertCancelledAsync(repeated));
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(new[] { "id", "status" },
            json.RootElement.EnumerateObject().Select(x => x.Name).ToArray());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual("cancelled", (await db.WaitlistEntries.SingleAsync(x => x.Id == _entry.Id)).Status);
        Assert.AreEqual("waiting", (await db.WaitlistEntries.SingleAsync(x => x.Id == _nextEntry.Id)).Status);
        Assert.AreEqual(1, await db.Enrollments.CountAsync(x => x.ClassId == _class.Id));
        Assert.AreEqual("active", (await db.Enrollments.SingleAsync(x => x.Id == _enrollment.Id)).Status);
        Assert.AreEqual(20, (await db.Passes.SingleAsync(x => x.UserId == _owner.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_UnknownAndForeignEntry_ReturnNotFoundWithoutChanges()
    {
        using var missing = await CancelWaitlistAsync(_owner, long.MaxValue);
        using var foreign = await CancelWaitlistAsync(_holder, _entry.Id);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        await AssertProblemAsync(foreign, HttpStatusCode.NotFound);

        _holder.Role = "admin";
        using var admin = await CancelWaitlistAsync(_holder, _entry.Id);
        await AssertProblemAsync(admin, HttpStatusCode.NotFound);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual("waiting", (await db.WaitlistEntries.SingleAsync(x => x.Id == _entry.Id)).Status);
    }

    [TestMethod]
    public async Task Cancel_WithInvalidJwtOrId_RejectsRequest()
    {
        using var anonymous = await _client.PostAsync(WaitlistUrl(_entry.Id), null);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, WaitlistUrl(_entry.Id));
        invalidRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        using var invalid = await _client.SendAsync(invalidRequest);
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalid.StatusCode);
        using var badId = await PostAsync(_client, _owner, "/enrollments/waitlist/not-a-number/cancel");
        await AssertProblemAsync(badId, HttpStatusCode.BadRequest);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancel_AfterClassStartOrCancellation_AllowsLeaving(bool classCancelled)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var entity = await db.Classes.SingleAsync(x => x.Id == _class.Id);
            entity.StartsAt = DateTime.UtcNow.AddHours(-1);
            entity.IsCancelled = classCancelled;
            await db.SaveChangesAsync();
        }

        using var response = await CancelWaitlistAsync(_owner, _entry.Id);
        await AssertCancelledAsync(response);
    }

    [TestMethod]
    public async Task Cancel_WhenEntryIsWaiting_UpdatesPositionAndAllowsRejoining()
    {
        using var cancelled = await CancelWaitlistAsync(_owner, _entry.Id);
        await AssertCancelledAsync(cancelled);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var repository = new WaitlistRepository(db.WaitlistEntries);
            var next = await repository.GetByIdForUserAsync(_nextEntry.Id, _next.Id);
            Assert.IsNotNull(next);
            Assert.AreEqual(0, await repository.CountWaitingBeforeAsync(next.ClassId, next.CreatedAt, next.Id));
            Assert.IsNull(await repository.GetByIdForUserAsync(_entry.Id, _holder.Id));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist");
        request.Headers.Authorization = Auth(_owner);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { classId = _class.Id, seats = 2 });
        using var rejoined = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Created, rejoined.StatusCode, await rejoined.Content.ReadAsStringAsync());
        var body = await rejoined.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        Assert.IsNotNull(body);
        Assert.AreNotEqual(_entry.Id, body.Id);
        Assert.AreEqual(2, body.Position);
    }

    [TestMethod]
    public async Task Cancel_BeforeSeatsAreFreed_SkipsDepartedUserAndPromotesNext()
    {
        using var cancelled = await CancelWaitlistAsync(_owner, _entry.Id);
        await AssertCancelledAsync(cancelled);
        using var released = await PostAsync(_client, _holder, EnrollmentCancelUrl());
        Assert.AreEqual(HttpStatusCode.OK, released.StatusCode, await released.Content.ReadAsStringAsync());
        await AssertDepartedUserWasSkippedAsync();
    }

    [TestMethod]
    public async Task Cancel_AfterPromotion_DoesNotCancelExistingEnrollmentOrRefundVisits()
    {
        using var released = await PostAsync(_client, _holder, EnrollmentCancelUrl());
        Assert.AreEqual(HttpStatusCode.OK, released.StatusCode, await released.Content.ReadAsStringAsync());
        using var cancelled = await CancelWaitlistAsync(_owner, _entry.Id);
        using var repeated = await CancelWaitlistAsync(_owner, _entry.Id);
        await AssertCancelledAsync(cancelled);
        await AssertCancelledAsync(repeated);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var promoted = await db.Enrollments.SingleAsync(x => x.UserId == _owner.Id && x.ClassId == _class.Id);
        Assert.AreEqual("active", promoted.Status);
        Assert.AreEqual(2, promoted.Seats);
        Assert.AreEqual(18, (await db.Passes.SingleAsync(x => x.UserId == _owner.Id)).RemainingVisits);
        Assert.AreEqual("cancelled", (await db.WaitlistEntries.SingleAsync(x => x.Id == _entry.Id)).Status);
    }

    [TestMethod]
    public async Task Cancel_ConcurrentRepeats_ReturnSameResponse()
    {
        var responses = await Task.WhenAll(
            CancelWaitlistAsync(_owner, _entry.Id), CancelWaitlistAsync(_owner, _entry.Id));
        try
        {
            Assert.AreEqual(await AssertCancelledAsync(responses[0]), await AssertCancelledAsync(responses[1]));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [TestMethod]
    public async Task Cancel_CommittedDuringPromotionTransaction_IsRespectedAfterRetry()
    {
        var gate = new PromotionGate();
        using var gatedFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWaitlistPromotionService>();
            services.AddSingleton(gate);
            services.AddScoped<WaitlistPromotionService>();
            services.AddScoped<IWaitlistPromotionService, DelayedPromotion>();
        }));
        using var client = gatedFactory.CreateClient();
        var releasingSeats = PostAsync(client, _holder, EnrollmentCancelUrl());
        try
        {
            await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancelled = await PostAsync(client, _owner, WaitlistUrl(_entry.Id))
                .WaitAsync(TimeSpan.FromSeconds(10));
            await AssertCancelledAsync(cancelled);
        }
        finally
        {
            gate.Release();
        }

        using var released = await releasingSeats.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(HttpStatusCode.OK, released.StatusCode, await released.Content.ReadAsStringAsync());
        Assert.IsTrue(gate.Attempts >= 2, "The stale promotion transaction must be retried.");
        await AssertDepartedUserWasSkippedAsync();
    }

    private async Task AssertDepartedUserWasSkippedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(0, await db.Enrollments.CountAsync(x => x.UserId == _owner.Id && x.ClassId == _class.Id));
        Assert.AreEqual(20, (await db.Passes.SingleAsync(x => x.UserId == _owner.Id)).RemainingVisits);
        Assert.AreEqual("cancelled", (await db.WaitlistEntries.SingleAsync(x => x.Id == _entry.Id)).Status);
        Assert.AreEqual("fulfilled", (await db.WaitlistEntries.SingleAsync(x => x.Id == _nextEntry.Id)).Status);
        var promoted = await db.Enrollments.SingleAsync(x => x.UserId == _next.Id && x.ClassId == _class.Id);
        Assert.AreEqual("active", promoted.Status);
        Assert.AreEqual(1, promoted.Seats);
        Assert.AreEqual(19, (await db.Passes.SingleAsync(x => x.UserId == _next.Id)).RemainingVisits);
    }

    private async Task<CancelWaitlistResponse> AssertCancelledAsync(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<CancelWaitlistResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(new CancelWaitlistResponse(_entry.Id, "cancelled"), body);
        return body;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.AreEqual(status, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual((int)status, json.RootElement.GetProperty("status").GetInt32());
    }

    private Task<HttpResponseMessage> CancelWaitlistAsync(User user, long id) =>
        PostAsync(_client, user, WaitlistUrl(id));

    private async Task<HttpResponseMessage> PostAsync(HttpClient client, User user, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = Auth(user);
        return await client.SendAsync(request);
    }

    private AuthenticationHeaderValue Auth(User user) => new("Bearer",
        _factory.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken);

    private static string WaitlistUrl(long id) => $"/enrollments/waitlist/{id}/cancel";
    private string EnrollmentCancelUrl() => $"/enrollments/{_enrollment.Id}/cancel";

    private static User NewUser() => new()
    {
        Email = $"cancel-waitlist-{Guid.NewGuid():N}@test.local", PasswordHash = "test",
        Role = "user", CreatedAt = DateTime.UtcNow
    };

    private sealed class PromotionGate
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public Task Started => _started.Task;
        public int Attempts => Volatile.Read(ref _attempts);
        public void Release() => _release.TrySetResult();

        public async Task PauseFirstAttemptAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                _started.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
        }
    }

    private sealed class DelayedPromotion(WaitlistPromotionService inner, PromotionGate gate)
        : IWaitlistPromotionService
    {
        public async Task PromoteAsync(long classId, DateTime now, CancellationToken ct)
        {
            await gate.PauseFirstAttemptAsync(ct);
            await inner.PromoteAsync(classId, now, ct);
        }
    }
}
