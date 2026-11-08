using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.Cancellation;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;
using WebApi.Contracts.Enrollments;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class EnrollmentHttpTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private long _userId;
    private long _otherUserId;
    private long _classId;
    private long _enrollmentId;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var user = NewUser();
        var otherUser = NewUser();
        var classEntity = new Class
        {
            Title = "HTTP test class", Room = new Room { Name = "HTTP test room" },
            StartsAt = DateTime.UtcNow.AddHours(4), DurationMinutes = 60, Capacity = 10
        };
        db.Users.AddRange(user, otherUser);
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
        _userId = user.Id;
        _otherUserId = otherUser.Id;
        _classId = classEntity.Id;
        _enrollmentId = enrollment.Id;
        Authenticate(user);
    }

    [TestCleanup]
    public void CleanUp()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [TestMethod]
    public async Task Create_ReturnsCreated_AndPersistsEnrollmentPassAndIdempotencyResult()
    {
        using var response = await CreateAsync("create-key", 2);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(_classId, body.ClassId);
        Assert.AreEqual(2, body.Seats);
        Assert.AreEqual("active", body.Status);

        var saved = await GetEnrollmentAsync(body.Id);
        Assert.AreEqual(_userId, saved.UserId);
        Assert.AreEqual(2, saved.Seats);
        Assert.AreEqual(3, (await GetPassAsync(_userId)).RemainingVisits);
        Assert.IsTrue(await HasIdempotencyRequestAsync("create-key"));
    }

    [TestMethod]
    public async Task Create_RepeatedKey_ReturnsOriginalResponseAndDoesNotDebitTwice()
    {
        using var first = await CreateAsync("repeat-key", 2);
        using var second = await CreateAsync("repeat-key", 2);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        var original = await first.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        var repeated = await second.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        Assert.IsNotNull(original);
        Assert.AreEqual(original, repeated);

        Assert.AreEqual(2, await GetEnrollmentCountAsync());
        Assert.AreEqual(3, (await GetPassAsync(_userId)).RemainingVisits);
    }

    [TestMethod]
    public async Task Create_ReusedKeyWithDifferentSeats_ReturnsConflictWithoutChanges()
    {
        using var first = await CreateAsync("conflict-key", 2);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        using var second = await CreateAsync("conflict-key", 3);

        await AssertReasonAsync(second, "idempotency-key-reused");
        Assert.AreEqual(2, await GetEnrollmentCountAsync());
        Assert.AreEqual(3, (await GetPassAsync(_userId)).RemainingVisits);
    }

    [DataTestMethod]
    [DataRow("{}", "key")]
    [DataRow("{\"classId\":1}", "key")]
    [DataRow("{\"classId\":1,\"seats\":0}", "key")]
    [DataRow("{\"classId\":1,\"seats\":\"invalid\"}", "key")]
    [DataRow("{\"classId\":1,\"seats\":1}", null)]
    [DataRow("", "key")]
    public async Task Create_InvalidRequest_ReturnsBadRequest(string json, string? key)
    {
        using var response = await SendRawCreateAsync(json, key);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Create_WhenClassIsMissing_ReturnsNotFound()
    {
        using var response = await SendRawCreateAsync($"{{\"classId\":{long.MaxValue},\"seats\":1}}", "missing-class");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Create_WhenCapacityIsExceeded_ReturnsConflict()
    {
        await PrepareBusinessRuleScenarioAsync("full");
        using var response = await CreateAsync("full-class", 1);
        await AssertReasonAsync(response, "capacity-exceeded");
    }

    [DataTestMethod]
    [DataRow("cancelled", "class-cancelled")]
    [DataRow("started", "class-started")]
    [DataRow("expired", "pass-expired")]
    [DataRow("exhausted", "pass-exhausted")]
    public async Task Create_WhenBusinessRuleFails_ReturnsConflict(string scenario, string reason)
    {
        await PrepareBusinessRuleScenarioAsync(scenario);

        using var response = await CreateAsync("rule-key", 1);
        await AssertReasonAsync(response, reason);
        Assert.AreEqual(1, await GetEnrollmentCountAsync());
        Assert.IsFalse(await HasIdempotencyRequestAsync());
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseThenDecrease_UpdatesPassAndReturnsDetails()
    {
        using var increase = await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats = 4 });
        Assert.AreEqual(HttpStatusCode.OK, increase.StatusCode);
        var increased = await increase.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(increased);
        Assert.AreEqual(4, increased.Seats);
        Assert.AreEqual(3, increased.Pass.RemainingVisits);
        Assert.AreEqual(_classId, increased.Class.Id);
        Assert.AreEqual("HTTP test room", increased.Class.Room.Name);

        using var decrease = await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats = 1 });
        Assert.AreEqual(HttpStatusCode.OK, decrease.StatusCode);
        var decreased = await decrease.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(decreased);
        Assert.AreEqual(1, decreased.Seats);
        Assert.AreEqual(6, decreased.Pass.RemainingVisits);

        Assert.AreEqual(1, (await GetEnrollmentAsync(_enrollmentId)).Seats);
        Assert.AreEqual(6, (await GetPassAsync(_userId)).RemainingVisits);
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("{\"seats\":0}")]
    [DataRow("{\"seats\":-1}")]
    [DataRow("{\"seats\":null}")]
    [DataRow("{\"seats\":1.5}")]
    [DataRow("{\"seats\":true}")]
    [DataRow("{\"seats\":2147483648}")]
    [DataRow("{\"seats\":")]
    [DataRow("{\"seats\":\"invalid\"}")]
    [DataRow("")]
    public async Task ChangeSeats_InvalidRequest_ReturnsBadRequest(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync($"/enrollments/{_enrollmentId}/seats", content);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(2, (await GetEnrollmentAsync(_enrollmentId)).Seats);
        Assert.AreEqual(5, (await GetPassAsync(_userId)).RemainingVisits);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task ChangeSeats_AlreadyCancelled_ReturnsConflict(int seats)
    {
        await PrepareCancelledEnrollmentAsync();
        using var response = await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats });
        await AssertReasonAsync(response, "enrollment-cancelled");
        Assert.AreEqual(2, (await GetEnrollmentAsync(_enrollmentId)).Seats);
        Assert.AreEqual(5, (await GetPassAsync(_userId)).RemainingVisits);
    }

    [TestMethod]
    public async Task ChangeSeats_WhenPassExhausted_ReturnsConflict()
    {
        await PrepareBusinessRuleScenarioAsync("exhausted");
        using var response = await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats = 3 });
        await AssertReasonAsync(response, "pass-exhausted");
    }

    [DataTestMethod]
    [DataRow("cancelled", "class-cancelled", 1)]
    [DataRow("cancelled", "class-cancelled", 2)]
    [DataRow("cancelled", "class-cancelled", 3)]
    [DataRow("started", "class-started", 1)]
    [DataRow("started", "class-started", 2)]
    [DataRow("started", "class-started", 3)]
    [DataRow("expired", "pass-expired", 3)]
    [DataRow("full", "capacity-exceeded", 3)]
    public async Task ChangeSeats_WhenBusinessRuleFails_ReturnsConflict(string scenario, string reason, int seats)
    {
        await PrepareBusinessRuleScenarioAsync(scenario);

        using var response = await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats });
        await AssertReasonAsync(response, reason);
        Assert.AreEqual(2, (await GetEnrollmentAsync(_enrollmentId)).Seats);
        Assert.AreEqual(5, (await GetPassAsync(_userId)).RemainingVisits);
    }

    [DataTestMethod]
    [DataRow(-1, 0)]
    [DataRow(-1, 5)]
    [DataRow(30, 0)]
    public async Task ChangeSeats_SameSeatsWithUnavailablePass_ReturnsOkWithoutChanges(int validDays, int visits)
    {
        await PreparePassForSeatChangeAsync(validDays, visits);
        var version = await GetCacheVersionAsync();
        using var response = await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats = 2 });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(2, body.Seats);
        Assert.AreEqual(visits, body.Pass.RemainingVisits);
        Assert.AreEqual(2, (await GetEnrollmentAsync(_enrollmentId)).Seats);
        Assert.AreEqual(visits, (await GetPassAsync(_userId)).RemainingVisits);
        Assert.AreEqual(version, await GetCacheVersionAsync());
    }

    [DataTestMethod]
    [DataRow(-1, 0)]
    [DataRow(-1, 5)]
    [DataRow(30, 0)]
    public async Task ChangeSeats_DecreaseWithUnavailablePass_ReturnsVisits(int validDays, int visits)
    {
        await PreparePassForSeatChangeAsync(validDays, visits);
        var originalPass = await GetPassAsync(_userId);
        using var response = await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats = 1 });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(1, body.Seats);
        Assert.AreEqual(visits + 1, body.Pass.RemainingVisits);
        Assert.AreEqual(1, (await GetEnrollmentAsync(_enrollmentId)).Seats);
        Assert.AreEqual(visits + 1, (await GetPassAsync(_userId)).RemainingVisits);
        Assert.AreEqual(originalPass.ValidUntil, body.Pass.ValidUntil);
    }

    [TestMethod]
    public async Task ChangeSeats_ExactCapacityAndPassBoundary_ReplaysAndRefundsWithFullCard()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            db.Enrollments.AddRange(
                new Enrollment { UserId = _otherUserId, ClassId = _classId, Seats = 3,
                    Status = "active", CreatedAt = DateTime.UtcNow },
                new Enrollment { UserId = _otherUserId, ClassId = _classId, Seats = 10,
                    Status = "cancelled", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var initial = await _client.GetFromJsonAsync<EnrollmentDetailsResponse>($"/enrollments/{_enrollmentId}");
        Assert.IsNotNull(initial);
        var expectedVersion = await GetCacheVersionAsync();
        foreach (var (seats, visits, occupied, changed) in new[]
                 { (7, 0, 10L, true), (7, 0, 10L, false), (1, 6, 4L, true), (1, 6, 4L, false) })
        {
            using var response = await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var card = await response.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
            Assert.IsNotNull(card);
            Assert.AreEqual(initial with { Seats = seats, Pass = initial.Pass with { RemainingVisits = visits } }, card);
            Assert.AreEqual(card, await _client.GetFromJsonAsync<EnrollmentDetailsResponse>($"/enrollments/{_enrollmentId}"));
            Assert.AreEqual(seats, (await GetEnrollmentAsync(_enrollmentId)).Seats);
            Assert.AreEqual(visits, (await GetPassAsync(_userId)).RemainingVisits);
            Assert.AreEqual(occupied, await ReadDatabaseAsync(db => db.Enrollments
                .Where(x => x.ClassId == _classId && x.Status == "active").SumAsync(x => (long)x.Seats)));
            if (changed) expectedVersion++;
            Assert.AreEqual(expectedVersion, await GetCacheVersionAsync());

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            CollectionAssert.AreEquivalent(new[] { "id", "status", "seats", "createdAt", "class", "pass" },
                json.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
            CollectionAssert.AreEquivalent(new[] { "id", "title", "startsAt", "durationMinutes", "isCancelled", "room" },
                json.RootElement.GetProperty("class").EnumerateObject().Select(x => x.Name).ToArray());
            CollectionAssert.AreEquivalent(new[] { "id", "name" },
                json.RootElement.GetProperty("class").GetProperty("room").EnumerateObject().Select(x => x.Name).ToArray());
            CollectionAssert.AreEquivalent(new[] { "remainingVisits", "validUntil" },
                json.RootElement.GetProperty("pass").EnumerateObject().Select(x => x.Name).ToArray());
        }
    }

    [DataTestMethod]
    [DataRow(180, false, 2)]
    [DataRow(121, false, 2)]
    [DataRow(119, false, 0)]
    [DataRow(30, false, 0)]
    [DataRow(180, true, 2)]
    [DataRow(30, true, 2)]
    [DataRow(0, true, 2)]
    [DataRow(-30, true, 2)]
    public async Task Cancel_RefundRules_ReturnsAndPersistsExpectedResult(int startMinutes, bool classCancelled, int refund)
    {
        await PrepareClassForCancellationAsync(startMinutes, classCancelled);
        using var response = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.AreEqual(new CancelEnrollmentResponse(_enrollmentId, "cancelled", refund), body);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(new[] { "id", "status", "refundedVisits" },
            json.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(_enrollmentId, json.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(refund, json.RootElement.GetProperty("refundedVisits").GetInt32());

        var enrollment = await GetEnrollmentAsync(_enrollmentId);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(2, enrollment.Seats);
        Assert.IsNotNull(enrollment.CancelledAt);
        Assert.AreEqual(refund, enrollment.RefundedVisits);
        Assert.AreEqual(5 + refund, (await GetPassAsync(_userId)).RemainingVisits);
        Assert.AreEqual(0L, await ReadDatabaseAsync(db => db.Enrollments
            .Where(x => x.ClassId == _classId && x.Status == "active").SumAsync(x => (long)x.Seats)));
    }

    [DataTestMethod]
    [DataRow(180, -1, 0, 2)]
    [DataRow(30, -1, 0, 0)]
    [DataRow(180, 30, 0, 2)]
    [DataRow(30, 30, 0, 0)]
    [DataRow(180, -1, 5, 2)]
    [DataRow(30, -1, 5, 0)]
    public async Task Cancel_WithExpiredOrEmptyPass_UsesOnlyClassRefundRule(
        int startMinutes, int validDays, int visits, int refund)
    {
        await PrepareClassForCancellationAsync(startMinutes, false);
        await PreparePassForSeatChangeAsync(validDays, visits);
        var validUntil = (await GetPassAsync(_userId)).ValidUntil;

        using var response = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new CancelEnrollmentResponse(_enrollmentId, "cancelled", refund),
            await response.Content.ReadFromJsonAsync<CancelEnrollmentResponse>());
        var pass = await GetPassAsync(_userId);
        Assert.AreEqual(visits + refund, pass.RemainingVisits);
        Assert.AreEqual(validUntil, pass.ValidUntil);
        Assert.AreEqual("cancelled", (await GetEnrollmentAsync(_enrollmentId)).Status);
    }

    [DataTestMethod]
    [DataRow(180, -30, false, 2)]
    [DataRow(30, -30, false, 0)]
    [DataRow(30, -30, true, 0)]
    [DataRow(180, 30, false, 2)]
    [DataRow(30, 180, false, 0)]
    public async Task Cancel_RepeatedAfterClassChanges_PreservesOriginalResult(
        int initialStartMinutes, int laterStartMinutes, bool laterCancelled, int refund)
    {
        await PrepareClassForCancellationAsync(initialStartMinutes, false);
        using var first = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var expected = await first.Content.ReadAsStringAsync();
        Assert.AreEqual(new CancelEnrollmentResponse(_enrollmentId, "cancelled", refund),
            await first.Content.ReadFromJsonAsync<CancelEnrollmentResponse>());
        var cancelledAt = (await GetEnrollmentAsync(_enrollmentId)).CancelledAt;
        var version = await GetCacheVersionAsync();

        await PrepareClassForCancellationAsync(laterStartMinutes, laterCancelled);
        using var replay = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);

        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode);
        Assert.AreEqual(expected, await replay.Content.ReadAsStringAsync());
        Assert.AreEqual(cancelledAt, (await GetEnrollmentAsync(_enrollmentId)).CancelledAt);
        Assert.AreEqual(refund, (await GetEnrollmentAsync(_enrollmentId)).RefundedVisits);
        Assert.AreEqual(5 + refund, (await GetPassAsync(_userId)).RemainingVisits);
        Assert.AreEqual(version, await GetCacheVersionAsync());
    }

    [TestMethod]
    public async Task Cancel_WhenClubCancels_RefundsOnlyEnrollmentRequestedByClient()
    {
        var otherEnrollmentId = await ReadDatabaseAsync(async db =>
        {
            db.Passes.Add(new Pass { UserId = _otherUserId, RemainingVisits = 4,
                ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) });
            var other = new Enrollment { UserId = _otherUserId, ClassId = _classId, Seats = 3,
                Status = "active", CreatedAt = DateTime.UtcNow };
            db.Enrollments.Add(other);
            await db.SaveChangesAsync();
            return other.Id;
        });
        await PrepareClassForCancellationAsync(-30, true);

        using var cardResponse = await _client.GetAsync($"/enrollments/{_enrollmentId}");
        Assert.AreEqual(HttpStatusCode.OK, cardResponse.StatusCode);
        var card = await cardResponse.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(card);
        Assert.IsTrue(card.Class.IsCancelled);
        Assert.AreEqual("active", card.Status);
        Assert.AreEqual(5, card.Pass.RemainingVisits);

        using var response = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new CancelEnrollmentResponse(_enrollmentId, "cancelled", 2),
            await response.Content.ReadFromJsonAsync<CancelEnrollmentResponse>());
        Assert.AreEqual(7, (await GetPassAsync(_userId)).RemainingVisits);
        var otherEnrollment = await GetEnrollmentAsync(otherEnrollmentId);
        Assert.AreEqual("active", otherEnrollment.Status);
        Assert.IsNull(otherEnrollment.CancelledAt);
        Assert.IsNull(otherEnrollment.RefundedVisits);
        Assert.AreEqual(4, (await GetPassAsync(_otherUserId)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_Twice_ReturnsSameResultAndRefundsOnlyOnce()
    {
        using var first = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        using var second = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.AreEqual(new CancelEnrollmentResponse(_enrollmentId, "cancelled", 2), firstBody);
        Assert.AreEqual(firstBody, secondBody);
        Assert.AreEqual(7, (await GetPassAsync(_userId)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_SeededCancelledEnrollment_ReplaysStoredRefundWithoutChangingPass()
    {
        var (enrollmentId, refundedVisits, remainingVisits) = await AuthenticateSeededCancelledEnrollmentAsync();

        using var first = await _client.PostAsync($"/enrollments/{enrollmentId}/cancel", null);
        using var second = await _client.PostAsync($"/enrollments/{enrollmentId}/cancel", null);

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.AreEqual(new CancelEnrollmentResponse(enrollmentId, "cancelled", refundedVisits), firstBody);
        Assert.AreEqual(firstBody, secondBody);
        var saved = await GetEnrollmentAsync(enrollmentId);
        Assert.AreEqual(refundedVisits, saved.RefundedVisits);
        Assert.AreEqual(remainingVisits, (await GetPassAsync(saved.UserId)).RemainingVisits);
    }

    [TestMethod]
    public async Task Cancel_RepeatedRequest_ChangesCacheVersionOnlyOnce()
    {
        var before = await GetCacheVersionAsync();

        using var first = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

        var afterFirst = await GetCacheVersionAsync();
        Assert.AreEqual(before + 1, afterFirst);

        using var second = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);

        var afterSecond = await GetCacheVersionAsync();
        Assert.AreEqual(afterFirst, afterSecond);
    }

    [TestMethod]
    public async Task Cancel_WhenClassStarted_ReturnsConflictWithoutChangingEnrollment()
    {
        await PrepareClassForCancellationAsync(-5, false);
        using var response = await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        await AssertReasonAsync(response, "class-started");
        Assert.AreEqual("active", (await GetEnrollmentAsync(_enrollmentId)).Status);
        Assert.AreEqual(5, (await GetPassAsync(_userId)).RemainingVisits);
    }

    [DataTestMethod]
    [DataRow("seats")]
    [DataRow("cancel")]
    public async Task ExistingEnrollmentEndpoints_WhenBelongsToAnotherUser_ReturnNotFound(string action)
    {
        await AuthenticateOtherUserAsync();
        using var response = action == "seats"
            ? await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats = 3 })
            : await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [DataTestMethod]
    [DataRow("seats")]
    [DataRow("cancel")]
    public async Task ExistingEnrollmentEndpoints_WhenEnrollmentDoesNotExist_ReturnNotFound(string action)
    {
        using var response = action == "seats"
            ? await _client.PostAsJsonAsync($"/enrollments/{long.MaxValue}/seats", new { seats = 3 })
            : await _client.PostAsync($"/enrollments/{long.MaxValue}/cancel", null);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [DataTestMethod]
    [DataRow("create")]
    [DataRow("seats")]
    [DataRow("cancel")]
    public async Task Endpoints_WithoutAuthentication_ReturnUnauthorized(string action)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        using var response = action switch
        {
            "create" => await CreateAsync("anonymous-key", 1),
            "seats" => await _client.PostAsJsonAsync($"/enrollments/{_enrollmentId}/seats", new { seats = 3 }),
            _ => await _client.PostAsync($"/enrollments/{_enrollmentId}/cancel", null)
        };
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task GetById_WhenEnrollmentExists_ReturnsFullCard()
    {
        using var response = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var card = await response.Content
            .ReadFromJsonAsync<EnrollmentDetailsResponse>();

        Assert.IsNotNull(card);
        Assert.AreEqual(_enrollmentId, card.Id);
        Assert.AreEqual("active", card.Status);
        Assert.AreEqual(2, card.Seats);

        Assert.AreEqual(_classId, card.Class.Id);
        Assert.AreEqual("HTTP test class", card.Class.Title);
        Assert.AreEqual("HTTP test room", card.Class.Room.Name);

        Assert.AreEqual(5, card.Pass.RemainingVisits);
    }

    [TestMethod]
    public async Task GetById_WhenEnrollmentIsCancelled_ReturnsCard()
    {
        await PrepareCancelledEnrollmentAsync(setCancellationTime: true);

        using var response = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var card = await response.Content
            .ReadFromJsonAsync<EnrollmentDetailsResponse>();

        Assert.IsNotNull(card);
        Assert.AreEqual("cancelled", card.Status);
        Assert.AreEqual(2, card.Seats);
    }

    [TestMethod]
    public async Task GetById_WhenClassIsCancelled_ReturnsCard()
    {
        await PrepareBusinessRuleScenarioAsync("cancelled");

        using var response = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var card = await response.Content
            .ReadFromJsonAsync<EnrollmentDetailsResponse>();

        Assert.IsNotNull(card);
        Assert.IsTrue(card.Class.IsCancelled);
        Assert.AreEqual("HTTP test room", card.Class.Room.Name);
    }

    [TestMethod]
    public async Task GetById_WhenEnrollmentDoesNotExist_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/enrollments/{long.MaxValue}");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task GetById_WhenEnrollmentBelongsToAnotherUser_ReturnsNotFound()
    {
        await AuthenticateOtherUserAsync();

        using var response = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task GetById_WithoutAuthentication_ReturnsUnauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        using var response = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task GetById_AfterChangingSeats_ReturnsUpdatedCard()
    {
        using var initialResponse = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, initialResponse.StatusCode);

        using var changeResponse = await _client.PostAsJsonAsync(
            $"/enrollments/{_enrollmentId}/seats",
            new { seats = 3 });

        Assert.AreEqual(HttpStatusCode.OK, changeResponse.StatusCode);

        using var response = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var card = await response.Content
            .ReadFromJsonAsync<EnrollmentDetailsResponse>();

        Assert.IsNotNull(card);
        Assert.AreEqual(3, card.Seats);
        Assert.AreEqual(4, card.Pass.RemainingVisits);
    }

    [TestMethod]
    public async Task GetById_AfterCreatingAnotherEnrollment_ReturnsUpdatedPass()
    {
        using var initialResponse = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, initialResponse.StatusCode);

        var initialCard = await initialResponse.Content
            .ReadFromJsonAsync<EnrollmentDetailsResponse>();

        Assert.IsNotNull(initialCard);
        Assert.AreEqual(5, initialCard.Pass.RemainingVisits);

        using var createResponse = await CreateAsync(
            "another-enrollment-key",
            seats: 1);

        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);

        using var updatedResponse = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);

        var updatedCard = await updatedResponse.Content
            .ReadFromJsonAsync<EnrollmentDetailsResponse>();

        Assert.IsNotNull(updatedCard);
        Assert.AreEqual(4, updatedCard.Pass.RemainingVisits);
    }

    [TestMethod]
    public async Task GetById_AfterCancellation_ReturnsCancelledCard()
    {
        using var initialResponse = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, initialResponse.StatusCode);

        using var cancelResponse = await _client.PostAsync(
            $"/enrollments/{_enrollmentId}/cancel",
            null);

        Assert.AreEqual(HttpStatusCode.OK, cancelResponse.StatusCode);

        using var updatedResponse = await _client.GetAsync(
            $"/enrollments/{_enrollmentId}");

        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);

        var card = await updatedResponse.Content
            .ReadFromJsonAsync<EnrollmentDetailsResponse>();

        Assert.IsNotNull(card);
        Assert.AreEqual("cancelled", card.Status);
        Assert.AreEqual(2, card.Seats);
        Assert.AreEqual(7, card.Pass.RemainingVisits);
    }

    private async Task<HttpResponseMessage> SendRawCreateAsync(string json, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    private Task PrepareBusinessRuleScenarioAsync(string scenario) => UpdateDataAsync((classEntity, _, pass) =>
    {
        if (scenario == "cancelled") classEntity.IsCancelled = true;
        if (scenario == "started") classEntity.StartsAt = DateTime.UtcNow.AddMinutes(-1);
        if (scenario == "expired") pass.ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        if (scenario == "exhausted") pass.RemainingVisits = 0;
        if (scenario == "full") classEntity.Capacity = 2;
    });

    private Task PreparePassForSeatChangeAsync(int validDays, int visits) => UpdateDataAsync((_, _, pass) =>
    {
        pass.ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(validDays);
        pass.RemainingVisits = visits;
    });

    private Task PrepareClassForCancellationAsync(int startMinutes, bool cancelled) => UpdateDataAsync((classEntity, _, _) =>
    {
        classEntity.StartsAt = DateTime.UtcNow.AddMinutes(startMinutes);
        classEntity.IsCancelled = cancelled;
    });

    private Task PrepareCancelledEnrollmentAsync(bool setCancellationTime = false) => UpdateDataAsync((_, enrollment, _) =>
    {
        enrollment.Status = "cancelled";
        if (setCancellationTime) enrollment.CancelledAt = DateTime.UtcNow;
    });

    private async Task<T> ReadDatabaseAsync<T>(Func<ClubDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        return await read(db);
    }

    private Task<Enrollment> GetEnrollmentAsync(long id) =>
        ReadDatabaseAsync(db => db.Enrollments.SingleAsync(x => x.Id == id));

    private Task<Pass> GetPassAsync(long userId) =>
        ReadDatabaseAsync(db => db.Passes.SingleAsync(x => x.UserId == userId));

    private Task<int> GetEnrollmentCountAsync() =>
        ReadDatabaseAsync(db => db.Enrollments.CountAsync(x => x.UserId == _userId));

    private Task<bool> HasIdempotencyRequestAsync(string? key = null) =>
        ReadDatabaseAsync(db => db.IdempotencyRequests.AnyAsync(x => x.UserId == _userId && (key == null || x.Key == key)));

    private Task<long> GetCacheVersionAsync() =>
        ReadDatabaseAsync(db => db.EnrollmentCardCacheVersions.Where(x => x.Id == _userId).Select(x => x.Version).SingleOrDefaultAsync());

    private async Task AuthenticateOtherUserAsync() =>
        Authenticate(await ReadDatabaseAsync(db => db.Users.SingleAsync(x => x.Id == _otherUserId)));

    private Task<(long EnrollmentId, int RefundedVisits, int RemainingVisits)> AuthenticateSeededCancelledEnrollmentAsync() =>
        ReadDatabaseAsync(async db =>
        {
            var enrollment = await db.Enrollments
                .Where(x => x.UserId <= 200 && x.Status == "cancelled" && x.RefundedVisits > 0)
                .OrderBy(x => x.Id).FirstAsync();
            var user = await db.Users.SingleAsync(x => x.Id == enrollment.UserId);
            var pass = await db.Passes.SingleAsync(x => x.UserId == user.Id);
            Authenticate(user);
            return (enrollment.Id, enrollment.RefundedVisits!.Value, pass.RemainingVisits);
        });

    private async Task<HttpResponseMessage> CreateAsync(string key, int seats)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments");
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(new { classId = _classId, seats });
        return await _client.SendAsync(request);
    }

    private async Task UpdateDataAsync(Action<Class, Enrollment, Pass> update)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var classEntity = await db.Classes.IgnoreQueryFilters().SingleAsync(x => x.Id == _classId);
        var enrollment = await db.Enrollments.SingleAsync(x => x.Id == _enrollmentId);
        var pass = await db.Passes.SingleAsync(x => x.UserId == _userId);
        update(classEntity, enrollment, pass);
        await db.SaveChangesAsync();
    }

    private void Authenticate(User user)
    {
        var token = _factory.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task AssertReasonAsync(HttpResponseMessage response, string expected)
    {
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(expected, document.RootElement.GetProperty("reason").GetString());
    }

    private static User NewUser() => new()
    {
        Email = $"http-{Guid.NewGuid():N}@test.local", PasswordHash = "test", Role = "user", CreatedAt = DateTime.UtcNow
    };
}
