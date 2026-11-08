using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;
using WebApi.Contracts.Enrollments;
using WebApi.Contracts.Enrollments.Create;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class EnrollmentCardHttpTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private User _owner = null!;
    private User _other = null!;
    private Class _class = null!;
    private Enrollment _enrollment = null!;
    private Pass _pass = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        _owner = NewUser(now);
        _other = NewUser(now);
        _class = new Class
        {
            Title = "Карточка: йога", Room = new Room { Name = "Зал № 4" },
            StartsAt = now.AddHours(4), DurationMinutes = 75, Capacity = 10
        };

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        db.Users.AddRange(_owner, _other);
        db.Classes.Add(_class);
        await db.SaveChangesAsync();
        _pass = new Pass
        {
            UserId = _owner.Id, RemainingVisits = 5,
            ValidUntil = DateOnly.FromDateTime(now).AddDays(30)
        };
        _enrollment = new Enrollment
        {
            UserId = _owner.Id, ClassId = _class.Id, Status = "active", Seats = 2, CreatedAt = now
        };
        db.Passes.Add(_pass);
        db.Enrollments.Add(_enrollment);
        await db.SaveChangesAsync();
        Authenticate(_client, _owner);
    }

    [TestCleanup]
    public void CleanUp()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [DataTestMethod]
    [DataRow("active", false)]
    [DataRow("active", true)]
    [DataRow("cancelled", false)]
    [DataRow("cancelled", true)]
    public async Task GetCard_WithAndWithoutCache_ReturnsExactJsonContract(string status, bool classCancelled)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var enrollment = await db.Enrollments.SingleAsync(x => x.Id == _enrollment.Id);
            enrollment.Status = status;
            enrollment.CancelledAt = status == "cancelled" ? DateTime.UtcNow : null;
            (await db.Classes.SingleAsync(x => x.Id == _class.Id)).IsCancelled = classCancelled;
            await db.SaveChangesAsync();
        }

        for (var read = 0; read < 2; read++)
        {
            using var response = await _client.GetAsync($"/enrollments/{_enrollment.Id}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var card = json.RootElement;
            AssertProperties(card, "id", "status", "seats", "createdAt", "class", "pass");
            Assert.AreEqual(_enrollment.Id, card.GetProperty("id").GetInt64());
            Assert.AreEqual(status, card.GetProperty("status").GetString());
            Assert.AreEqual(2, card.GetProperty("seats").GetInt32());
            Assert.AreEqual(_enrollment.CreatedAt, card.GetProperty("createdAt").GetDateTime());
            Assert.AreEqual(DateTimeKind.Utc, card.GetProperty("createdAt").GetDateTime().Kind);

            var lesson = card.GetProperty("class");
            AssertProperties(lesson, "id", "title", "startsAt", "durationMinutes", "isCancelled", "room");
            Assert.AreEqual(_class.Id, lesson.GetProperty("id").GetInt64());
            Assert.AreEqual(_class.Title, lesson.GetProperty("title").GetString());
            Assert.AreEqual(_class.StartsAt, lesson.GetProperty("startsAt").GetDateTime());
            Assert.AreEqual(DateTimeKind.Utc, lesson.GetProperty("startsAt").GetDateTime().Kind);
            Assert.AreEqual(75, lesson.GetProperty("durationMinutes").GetInt32());
            Assert.AreEqual(classCancelled, lesson.GetProperty("isCancelled").GetBoolean());

            var room = lesson.GetProperty("room");
            AssertProperties(room, "id", "name");
            Assert.AreEqual(_class.Room.Id, room.GetProperty("id").GetInt64());
            Assert.AreEqual(_class.Room.Name, room.GetProperty("name").GetString());

            var pass = card.GetProperty("pass");
            AssertProperties(pass, "remainingVisits", "validUntil");
            Assert.AreEqual(5, pass.GetProperty("remainingVisits").GetInt32());
            Assert.AreEqual(_pass.ValidUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                pass.GetProperty("validUntil").GetString());
        }
    }

    [DataTestMethod]
    [DataRow(-1, 5)]
    [DataRow(30, 0)]
    [DataRow(-1, 0)]
    public async Task GetCard_WhenClassHasStartedAndPassIsInvalid_ReturnsCard(int validDays, int visits)
    {
        var validUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(validDays);
        var startsAt = _class.StartsAt.AddDays(-1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var pass = await db.Passes.SingleAsync(x => x.UserId == _owner.Id);
            pass.ValidUntil = validUntil;
            pass.RemainingVisits = visits;
            (await db.Classes.SingleAsync(x => x.Id == _class.Id)).StartsAt = startsAt;
            await db.SaveChangesAsync();
        }

        var card = await GetCardAsync(_client, _enrollment.Id);
        Assert.AreEqual("active", card.Status);
        Assert.AreEqual(2, card.Seats);
        Assert.AreEqual(startsAt, card.Class.StartsAt);
        Assert.AreEqual(visits, card.Pass.RemainingVisits);
        Assert.AreEqual(validUntil, card.Pass.ValidUntil);
    }

    [DataTestMethod]
    [DataRow("user")]
    [DataRow("admin")]
    public async Task GetCard_WhenOwnerHasWarmedCache_OtherUserStillGetsNotFound(string role)
    {
        var original = await GetCardAsync(_client, _enrollment.Id);
        _other.Role = role;
        Authenticate(_client, _other);

        using var foreign = await _client.GetAsync($"/enrollments/{_enrollment.Id}");
        using var missing = await _client.GetAsync($"/enrollments/{long.MaxValue}");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        Authenticate(_client, _owner);
        Assert.AreEqual(original, await GetCardAsync(_client, _enrollment.Id));
    }

    [DataTestMethod]
    [DataRow("create", 2, 2, "active")]
    [DataRow("increase", 1, 4, "active")]
    [DataRow("decrease", 4, 1, "active")]
    [DataRow("cancel", 5, 2, "cancelled")]
    [DataRow("late-cancel", 3, 2, "cancelled")]
    public async Task GetCard_AfterOtherBookingChangesOnAnotherApiInstance_RefreshesAllCards(
        string operation, int expectedVisits, int expectedSeats, string expectedStatus)
    {
        var anotherClass = new Class
        {
            Title = "Другое занятие", RoomId = _class.Room.Id,
            StartsAt = DateTime.UtcNow.AddHours(operation == "late-cancel" ? 1 : 4),
            DurationMinutes = 45, Capacity = 10
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            db.Classes.Add(anotherClass);
            await db.SaveChangesAsync();
        }
        using var created = await CreateAsync(_client, anotherClass.Id, 2);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var sibling = await created.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        Assert.IsNotNull(sibling);

        using var anotherFactory = new ApiApplicationFactory();
        using var anotherClient = anotherFactory.CreateClient();
        Authenticate(anotherClient, _owner);
        foreach (var client in new[] { _client, anotherClient })
        {
            Assert.AreEqual(3, (await GetCardAsync(client, _enrollment.Id)).Pass.RemainingVisits);
            Assert.AreEqual(3, (await GetCardAsync(client, sibling.Id)).Pass.RemainingVisits);
        }

        using var change = operation switch
        {
            "create" => await CreateAsync(anotherClient, anotherClass.Id, 1),
            "increase" or "decrease" => await anotherClient.PostAsJsonAsync(
                $"/enrollments/{sibling.Id}/seats", new { seats = expectedSeats }),
            _ => await anotherClient.PostAsync($"/enrollments/{sibling.Id}/cancel", null)
        };
        Assert.AreEqual(operation == "create" ? HttpStatusCode.Created : HttpStatusCode.OK,
            change.StatusCode, await change.Content.ReadAsStringAsync());

        foreach (var client in new[] { _client, anotherClient })
        {
            var unchanged = await GetCardAsync(client, _enrollment.Id);
            Assert.AreEqual(2, unchanged.Seats);
            Assert.AreEqual("active", unchanged.Status);
            Assert.AreEqual(_enrollment.CreatedAt, unchanged.CreatedAt);
            Assert.AreEqual(expectedVisits, unchanged.Pass.RemainingVisits);
            Assert.AreEqual(_pass.ValidUntil, unchanged.Pass.ValidUntil);

            var updated = await GetCardAsync(client, sibling.Id);
            Assert.AreEqual(expectedSeats, updated.Seats);
            Assert.AreEqual(expectedStatus, updated.Status);
            Assert.AreEqual(sibling.CreatedAt, updated.CreatedAt);
            Assert.AreEqual(expectedVisits, updated.Pass.RemainingVisits);
        }
    }

    [TestMethod]
    public async Task GetCard_WhenCached_StillEnforcesRateLimit()
    {
        for (var request = 1; request <= 60; request++)
        {
            using var allowed = await _client.GetAsync($"/enrollments/{_enrollment.Id}");
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, $"Request {request}");
        }

        using var limited = await _client.GetAsync($"/enrollments/{_enrollment.Id}");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.IsNotNull(limited.Headers.RetryAfter?.Delta);
        Assert.IsTrue(limited.Headers.RetryAfter.Delta >= TimeSpan.FromSeconds(1));
        Assert.IsTrue(limited.Headers.RetryAfter.Delta <= TimeSpan.FromSeconds(60));
        using var problem = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.AreEqual(429, problem.RootElement.GetProperty("status").GetInt32());
    }

    private void Authenticate(HttpClient client, User user)
    {
        var token = _factory.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<EnrollmentDetailsResponse> GetCardAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var card = await response.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(card);
        return card;
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, long classId, int seats)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Content = JsonContent.Create(new { classId, seats });
        return await client.SendAsync(request);
    }

    private static void AssertProperties(JsonElement value, params string[] expected) =>
        CollectionAssert.AreEquivalent(expected, value.EnumerateObject().Select(x => x.Name).ToArray());

    private static User NewUser(DateTime createdAt) => new()
    {
        Email = $"card-{Guid.NewGuid():N}@test.local", PasswordHash = "test", Role = "user", CreatedAt = createdAt
    };
}
