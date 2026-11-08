using WebApi.Contracts.Enrollments.Waitlist;
using WebApi.Services.Waitlist;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Auth;
using WebApi.Contracts.Enrollments;
using WebApi.Contracts.Enrollments.Cancellation;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class WaitlistHttpTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private User _user = null!;
    private User _other = null!;
    private Class _class = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        _user = NewUser();
        _other = NewUser();
        _class = new Class { Title = "Waitlist", Room = new Room { Name = "Waitlist room" },
            StartsAt = DateTime.UtcNow.AddDays(3), DurationMinutes = 60, Capacity = 10 };
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        db.Users.AddRange(_user, _other);
        db.Classes.Add(_class);
        await db.SaveChangesAsync();
        db.Passes.Add(new Pass { UserId = _user.Id, RemainingVisits = 12, ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) });
        db.Passes.Add(new Pass { UserId = _other.Id, RemainingVisits = 8, ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) });
        db.Enrollments.Add(new Enrollment { UserId = _user.Id, ClassId = _class.Id, Seats = 9,
            Status = "active", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Authenticate(_user);
    }

    [TestCleanup]
    public void Cleanup() { _client.Dispose(); _factory.Dispose(); }

    [DataTestMethod]
    [DataRow(4320, 9)]
    [DataRow(30, 0)]
    public async Task Cancel_WithWaitingUser_PromotesOnlyOnce(int startMinutes, int refund)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            (await db.Classes.SingleAsync(x => x.Id == _class.Id)).StartsAt = DateTime.UtcNow.AddMinutes(startMinutes);
            await db.SaveChangesAsync();
        }
        Authenticate(_other);
        using var joined = await Join("promotion", _class.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, joined.StatusCode);
        var waitlist = await joined.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        Assert.IsNotNull(waitlist);

        long enrollmentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            enrollmentId = await db.Enrollments.Where(x => x.UserId == _user.Id && x.ClassId == _class.Id)
                .Select(x => x.Id).SingleAsync();
        }

        Authenticate(_user);
        using var cancelled = await _client.PostAsync($"/enrollments/{enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode, await cancelled.Content.ReadAsStringAsync());
        using var repeated = await _client.PostAsync($"/enrollments/{enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
        Assert.AreEqual(new CancelEnrollmentResponse(enrollmentId, "cancelled", refund),
            await cancelled.Content.ReadFromJsonAsync<CancelEnrollmentResponse>());
        Assert.AreEqual(await cancelled.Content.ReadAsStringAsync(), await repeated.Content.ReadAsStringAsync());
        using var checkScope = _factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual("fulfilled", (await check.WaitlistEntries.SingleAsync(x => x.Id == waitlist.Id)).Status);
        var promoted = await check.Enrollments.Where(x => x.ClassId == _class.Id && x.UserId == _other.Id).ToListAsync();
        Assert.AreEqual(1, promoted.Count);
        Assert.AreEqual("active", promoted[0].Status);
        Assert.AreEqual(2, promoted[0].Seats);
        Assert.AreEqual(6, (await check.Passes.SingleAsync(x => x.UserId == _other.Id)).RemainingVisits);
        Assert.AreEqual(12 + refund, (await check.Passes.SingleAsync(x => x.UserId == _user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_WithPromotion_RefreshesWaitingUsersCachedPass()
    {
        long existingId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var existing = new Enrollment
            {
                UserId = _other.Id, ClassId = _class.Id, Seats = 1, Status = "active", CreatedAt = DateTime.UtcNow
            };
            db.Enrollments.Add(existing);
            await db.SaveChangesAsync();
            existingId = existing.Id;
        }

        Authenticate(_other);
        using var before = await _client.GetAsync($"/enrollments/{existingId}");
        Assert.AreEqual(HttpStatusCode.OK, before.StatusCode);
        Assert.AreEqual(8, (await before.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>())!.Pass.RemainingVisits);
        using var joined = await Join("cached-promotion", _class.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, joined.StatusCode);

        Authenticate(_user);
        using var cancelled = await _client.PostAsync($"/enrollments/{await OriginalEnrollmentId()}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode, await cancelled.Content.ReadAsStringAsync());

        Authenticate(_other);
        using var after = await _client.GetAsync($"/enrollments/{existingId}");
        Assert.AreEqual(HttpStatusCode.OK, after.StatusCode);
        var card = await after.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(card);
        Assert.AreEqual(6, card.Pass.RemainingVisits);
        Assert.AreEqual(1, card.Seats);
        using var checkScope = _factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(1L, (await check.EnrollmentCardCacheVersions.SingleAsync(x => x.Id == _other.Id)).Version);
        Assert.AreEqual(1L, (await check.EnrollmentCardCacheVersions.SingleAsync(x => x.Id == _user.Id)).Version);
    }

    [TestMethod]
    public async Task Cancel_WhenPromotionFails_RollsBackCancellationAndPromotion()
    {
        var originalId = await OriginalEnrollmentId();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            db.WaitlistEntries.Add(new WaitlistEntry { UserId = _other.Id, ClassId = _class.Id, Seats = 2,
                Status = "waiting", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using var failingFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWaitlistPromotionService>();
            services.AddScoped<WaitlistPromotionService>();
            services.AddScoped<IWaitlistPromotionService, FailingPromotion>();
        }));
        using var client = failingFactory.CreateClient();
        var token = failingFactory.Services.GetRequiredService<AccessTokenService>().Create(_user).AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.PostAsync($"/enrollments/{originalId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        using var checkScope = _factory.Services.CreateScope();
        var dbCheck = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual("active", (await dbCheck.Enrollments.SingleAsync(x => x.Id == originalId)).Status);
        Assert.AreEqual("waiting", (await dbCheck.WaitlistEntries.SingleAsync(x => x.ClassId == _class.Id)).Status);
        Assert.AreEqual(0, await dbCheck.Enrollments.CountAsync(x => x.ClassId == _class.Id && x.UserId == _other.Id));
        Assert.AreEqual(12, (await dbCheck.Passes.SingleAsync(x => x.UserId == _user.Id)).RemainingVisits);
        Assert.AreEqual(8, (await dbCheck.Passes.SingleAsync(x => x.UserId == _other.Id)).RemainingVisits);
        Assert.IsFalse(await dbCheck.EnrollmentCardCacheVersions.AnyAsync(x => x.Id == _other.Id || x.Id == _user.Id));
    }

    private sealed class FailingPromotion(WaitlistPromotionService inner) : IWaitlistPromotionService
    {
        public async Task PromoteAsync(long classId, DateTime now, CancellationToken ct)
        {
            await inner.PromoteAsync(classId, now, ct);
            throw new InvalidOperationException("Simulated promotion failure");
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancel_WithMultipleWaitingUsers_PromotesInFifoOrder(bool sameCreatedAt)
    {
        var third = NewUser();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            db.Users.Add(third);
            await db.SaveChangesAsync();
            db.Passes.Add(new Pass { UserId = third.Id, RemainingVisits = 7, ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) });
            var firstCreatedAt = DateTime.UtcNow.AddMinutes(-2);
            db.WaitlistEntries.AddRange(
                new WaitlistEntry { UserId = _other.Id, ClassId = _class.Id, Seats = 2, Status = "waiting", CreatedAt = firstCreatedAt },
                new WaitlistEntry { UserId = third.Id, ClassId = _class.Id, Seats = 3, Status = "waiting",
                    CreatedAt = sameCreatedAt ? firstCreatedAt : firstCreatedAt.AddMinutes(1) });
            await db.SaveChangesAsync();
        }
        var id = await OriginalEnrollmentId();
        using var result = await _client.PostAsync($"/enrollments/{id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
        using var checkScope = _factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var entries = await check.WaitlistEntries.Where(x => x.ClassId == _class.Id).OrderBy(x => x.CreatedAt).ToListAsync();
        CollectionAssert.AreEqual(new[] { "fulfilled", "fulfilled" }, entries.Select(x => x.Status).ToArray());
        var promoted = await check.Enrollments.Where(x => x.ClassId == _class.Id && x.Status == "active").OrderBy(x => x.Id).ToListAsync();
        CollectionAssert.AreEqual(new[] { _other.Id, third.Id }, promoted.Select(x => x.UserId).ToArray());
        Assert.AreEqual(6, (await check.Passes.SingleAsync(x => x.UserId == _other.Id)).RemainingVisits);
        Assert.AreEqual(4, (await check.Passes.SingleAsync(x => x.UserId == third.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_SameEnrollmentConcurrently_RefundsAndPromotesOnlyOnce()
    {
        var enrollmentId = await OriginalEnrollmentId();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            db.WaitlistEntries.Add(new WaitlistEntry { UserId = _other.Id, ClassId = _class.Id,
                Seats = 2, Status = "waiting", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 12).Select(async _ =>
        {
            await start.Task;
            return await _client.PostAsync($"/enrollments/{enrollmentId}/cancel", null);
        }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(calls);
        try
        {
            foreach (var response in responses)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                Assert.AreEqual(new CancelEnrollmentResponse(enrollmentId, "cancelled", 9),
                    await response.Content.ReadFromJsonAsync<CancelEnrollmentResponse>());
            }
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            Assert.AreEqual(21, (await db.Passes.SingleAsync(x => x.UserId == _user.Id)).RemainingVisits);
            Assert.AreEqual(6, (await db.Passes.SingleAsync(x => x.UserId == _other.Id)).RemainingVisits);
            var active = await db.Enrollments.Where(x => x.ClassId == _class.Id && x.Status == "active").SingleAsync();
            Assert.AreEqual(_other.Id, active.UserId);
            Assert.AreEqual(2, active.Seats);
            Assert.AreEqual("fulfilled", (await db.WaitlistEntries.SingleAsync(x => x.ClassId == _class.Id)).Status);
            Assert.AreEqual(1L, (await db.EnrollmentCardCacheVersions.SingleAsync(x => x.Id == _user.Id)).Version);
            Assert.AreEqual(1L, (await db.EnrollmentCardCacheVersions.SingleAsync(x => x.Id == _other.Id)).Version);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [DataTestMethod]
    [DataRow("seats")]
    [DataRow("exhausted")]
    [DataRow("expired")]
    public async Task Cancel_WhenFirstCannotBePromoted_DoesNotSkipQueue(string reason)
    {
        var third = NewUser();
        long enrollmentToCancel;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var original = await db.Enrollments.SingleAsync(x => x.UserId == _user.Id && x.ClassId == _class.Id);
            original.Seats = 2;
            db.Enrollments.Add(new Enrollment { UserId = _user.Id, ClassId = _class.Id, Seats = 7, Status = "active", CreatedAt = DateTime.UtcNow });
            db.Users.Add(third);
            var pass = await db.Passes.SingleAsync(x => x.UserId == _other.Id);
            if (reason == "exhausted") pass.RemainingVisits = 0;
            if (reason == "expired") pass.ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
            await db.SaveChangesAsync();
            db.Passes.Add(new Pass { UserId = third.Id, RemainingVisits = 10, ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) });
            db.WaitlistEntries.AddRange(
                new WaitlistEntry { UserId = _other.Id, ClassId = _class.Id, Seats = reason == "seats" ? 4 : 2, Status = "waiting", CreatedAt = DateTime.UtcNow.AddMinutes(-2) },
                new WaitlistEntry { UserId = third.Id, ClassId = _class.Id, Seats = 1, Status = "waiting", CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
            enrollmentToCancel = original.Id;
        }
        using var response = await _client.PostAsync($"/enrollments/{enrollmentToCancel}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var checkScope = _factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(2, await check.WaitlistEntries.CountAsync(x => x.ClassId == _class.Id && x.Status == "waiting"));
        Assert.AreEqual(0, await check.Enrollments.CountAsync(x => x.ClassId == _class.Id && (x.UserId == _other.Id || x.UserId == third.Id)));
    }

    [TestMethod]
    public async Task Cancel_WhenClassCancelled_DoesNotPromoteWaitingUser()
    {
        var id = await OriginalEnrollmentId();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            db.WaitlistEntries.Add(new WaitlistEntry { UserId = _other.Id, ClassId = _class.Id, Seats = 2, Status = "waiting", CreatedAt = DateTime.UtcNow });
            (await db.Classes.SingleAsync(x => x.Id == _class.Id)).IsCancelled = true;
            await db.SaveChangesAsync();
        }
        using var response = await _client.PostAsync($"/enrollments/{id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var checkScope = _factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual("waiting", (await check.WaitlistEntries.SingleAsync(x => x.ClassId == _class.Id)).Status);
        Assert.AreEqual(0, await check.Enrollments.IgnoreQueryFilters().CountAsync(x => x.ClassId == _class.Id && x.UserId == _other.Id));
    }

    [TestMethod]
    public async Task Cancel_ConcurrentCancellations_PromoteOnlyOnceAndRespectCapacity()
    {
        var firstId = await OriginalEnrollmentId();
        long secondId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var first = await db.Enrollments.SingleAsync(x => x.Id == firstId);
            first.Seats = 4;
            var second = new Enrollment { UserId = _user.Id, ClassId = _class.Id, Seats = 5, Status = "active", CreatedAt = DateTime.UtcNow };
            db.Enrollments.Add(second);
            db.WaitlistEntries.Add(new WaitlistEntry { UserId = _other.Id, ClassId = _class.Id, Seats = 3, Status = "waiting", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            secondId = second.Id;
        }
        var responses = await Task.WhenAll(_client.PostAsync($"/enrollments/{firstId}/cancel", null), _client.PostAsync($"/enrollments/{secondId}/cancel", null));
        try
        {
            foreach (var response in responses) Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var active = await db.Enrollments.Where(x => x.ClassId == _class.Id && x.Status == "active").ToListAsync();
            Assert.AreEqual(1, active.Count(x => x.UserId == _other.Id));
            Assert.IsTrue(active.Sum(x => x.Seats) <= _class.Capacity);
            Assert.AreEqual(5, (await db.Passes.SingleAsync(x => x.UserId == _other.Id)).RemainingVisits);
            Assert.AreEqual("fulfilled", (await db.WaitlistEntries.SingleAsync(x => x.ClassId == _class.Id)).Status);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private async Task<long> OriginalEnrollmentId()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        return await db.Enrollments.Where(x => x.UserId == _user.Id && x.ClassId == _class.Id).Select(x => x.Id).SingleAsync();
    }

    [TestMethod]
    public async Task Join_SuccessAndRepeat_PreservesResponseAndDatabaseState()
    {
        using var first = await Join("same", _class.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        var original = await first.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        Assert.IsNotNull(original);
        Assert.AreEqual(1, original.Position);
        Assert.AreEqual(_class.Id, original.ClassId);
        Assert.AreEqual(2, original.Seats);
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(new[] { "id", "classId", "seats", "position", "createdAt" },
            json.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Authenticate(_other);
        using var second = await Join("other", _class.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.AreEqual(2, (await second.Content.ReadFromJsonAsync<JoinWaitlistResponse>())!.Position);
        Authenticate(_user);
        using var replay = await Join("same", _class.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.AreEqual(original, await replay.Content.ReadFromJsonAsync<JoinWaitlistResponse>());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var entry = await db.WaitlistEntries.SingleAsync(x => x.Id == original.Id);
        Assert.AreEqual("waiting", entry.Status);
        Assert.AreEqual(_user.Id, entry.UserId);
        Assert.AreEqual(12, (await db.Passes.SingleAsync(x => x.UserId == _user.Id)).RemainingVisits);
        Assert.AreEqual(2, await db.WaitlistEntries.CountAsync(x => x.ClassId == _class.Id));
        Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => x.UserId == _user.Id && x.Operation == "join-waitlist" && x.Key == "same"));
    }

    [TestMethod]
    public async Task Join_WithInvalidRequests_RejectsWithoutChanges()
    {
        foreach (var (key, body) in new (string?, string)[] {
            (null, $"{{\"classId\":{_class.Id},\"seats\":2}}"), ("", $"{{\"classId\":{_class.Id},\"seats\":2}}"),
            ("   ", $"{{\"classId\":{_class.Id},\"seats\":2}}"), ("k", "{}"),
            ("k", $"{{\"classId\":{_class.Id}}}"), ("k", $"{{\"classId\":{_class.Id},\"seats\":0}}"),
            ("k", $"{{\"classId\":{_class.Id},\"seats\":-1}}"),
            ("k", "{\"classId\":\"invalid\",\"seats\":2}"), ("k", "{"),
        })
        {
            using var response = await Send(key, body);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"{key}: {body}");
        }
        using (var missingBody = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist"))
        {
            missingBody.Headers.Add("Idempotency-Key", "no-body");
            using var response = await _client.SendAsync(missingBody);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        _client.DefaultRequestHeaders.Authorization = null;
        using var unauthorized = await Join("auth", _class.Id, 2);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(0, await db.WaitlistEntries.CountAsync(x => x.ClassId == _class.Id));
        Assert.AreEqual(0, await db.IdempotencyRequests.CountAsync(x => x.UserId == _user.Id && x.Operation == "join-waitlist"));
    }

    [TestMethod]
    public async Task Join_WithBusinessConflicts_RejectsWithoutSavingFailures()
    {
        using var missing = await Join("missing", long.MaxValue, 2);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        using var available = await Join("available", _class.Id, 1);
        await AssertConflict(available, "seats-available");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var entity = await db.Classes.SingleAsync(x => x.Id == _class.Id);
            entity.StartsAt = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }
        using var started = await Join("started", _class.Id, 2);
        await AssertConflict(started, "class-started");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var entity = await db.Classes.SingleAsync(x => x.Id == _class.Id);
            entity.IsCancelled = true;
            await db.SaveChangesAsync();
        }
        using var cancelled = await Join("cancelled", _class.Id, 2);
        await AssertConflict(cancelled, "class-cancelled");
        using var checkScope = _factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(0, await check.WaitlistEntries.CountAsync(x => x.ClassId == _class.Id));
        Assert.AreEqual(0, await check.IdempotencyRequests.CountAsync(x => x.UserId == _user.Id && x.Operation == "join-waitlist"));
    }

    [TestMethod]
    public async Task Join_WithExistingEntries_CountsOnlyWaitingEntriesForSameClass()
    {
        var earlier = DateTime.UtcNow.AddHours(-1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var third = NewUser();
            db.Users.Add(third);
            await db.SaveChangesAsync();
            db.WaitlistEntries.AddRange(
                new WaitlistEntry { UserId = _other.Id, ClassId = _class.Id, Seats = 2, Status = "waiting", CreatedAt = earlier },
                new WaitlistEntry { UserId = third.Id, ClassId = _class.Id, Seats = 2, Status = "waiting", CreatedAt = earlier },
                new WaitlistEntry { UserId = _user.Id, ClassId = _class.Id, Seats = 2, Status = "cancelled", CreatedAt = earlier });
            await db.SaveChangesAsync();
        }

        using var response = await Join("position", _class.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(3, (await response.Content.ReadFromJsonAsync<JoinWaitlistResponse>())!.Position);
    }

    [TestMethod]
    public async Task Join_ExistingWaitingAndKeyReuse_ReturnConflicts()
    {
        using var first = await Join("first", _class.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await Join("new", _class.Id, 2);
        await AssertConflict(duplicate, "already-waiting");
        using var changed = await Join("first", _class.Id, 3);
        await AssertConflict(changed, "idempotency-key-reused");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(1, await db.WaitlistEntries.CountAsync(x => x.ClassId == _class.Id && x.UserId == _user.Id));
        Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => x.UserId == _user.Id && x.Operation == "join-waitlist"));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Join_ParallelRequests_ReturnReplayOrConflict(bool sameKey)
    {
        var responses = await Task.WhenAll(Join("parallel-1", _class.Id, 2), Join(sameKey ? "parallel-1" : "parallel-2", _class.Id, 2));
        try
        {
            Assert.AreEqual(sameKey ? 2 : 1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
            if (sameKey)
                Assert.AreEqual(await responses[0].Content.ReadFromJsonAsync<JoinWaitlistResponse>(), await responses[1].Content.ReadFromJsonAsync<JoinWaitlistResponse>());
            else
                await AssertConflict(responses.Single(x => x.StatusCode != HttpStatusCode.Created), "already-waiting");
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            Assert.AreEqual(1, await db.WaitlistEntries.CountAsync(x => x.ClassId == _class.Id && x.UserId == _user.Id));
            Assert.AreEqual(1, await db.IdempotencyRequests.CountAsync(x => x.UserId == _user.Id && x.Operation == "join-waitlist"));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private static async Task AssertConflict(HttpResponseMessage response, string reason)
    {
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(reason, body.RootElement.GetProperty("reason").GetString());
    }
    private Task<HttpResponseMessage> Join(string key, long classId, int seats) => Send(key, JsonSerializer.Serialize(new { classId, seats }));
    private async Task<HttpResponseMessage> Send(string? key, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist");
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }
    private void Authenticate(User user) => _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken);
    private static User NewUser() => new() { Email = $"waitlist-{Guid.NewGuid():N}@test.local", PasswordHash = "test", Role = "user", CreatedAt = DateTime.UtcNow };
}
