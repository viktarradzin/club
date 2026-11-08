using WebApi.Contracts.Enrollments.Create;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Contracts.Analytics;
using WebApi.Services.Caching;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class AnalyticsHttpTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private User _admin = null!;
    private User _user = null!;
    private Room _room = null!;
    private Room _otherRoom = null!;
    private Class _class = null!;
    private DateOnly _date;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        _date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        _admin = NewUser("admin");
        _user = NewUser("user");
        _room = new Room { Name = "Analytics first room" };
        _otherRoom = new Room { Name = "Analytics second room" };
        var midnight = _date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        _class = NewClass(_room, midnight.AddHours(10), 10);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        db.Users.AddRange(_admin, _user);
        db.Classes.AddRange(_class,
            NewClass(_room, midnight.AddHours(23).AddMinutes(59), 20),
            NewClass(_room, midnight.AddDays(1), 30),
            NewClass(_otherRoom, midnight.AddHours(12), 40),
            NewClass(_otherRoom, midnight.AddDays(1).AddHours(12), 50),
            NewClass(_room, midnight.AddHours(8), 100, cancelled: true));
        await db.SaveChangesAsync();
        db.Passes.Add(new Pass { UserId = _user.Id, RemainingVisits = 20, ValidUntil = _date.AddDays(30) });
        db.Enrollments.AddRange(
            NewEnrollment(_user.Id, _class.Id, 3, "active"),
            NewEnrollment(_user.Id, _class.Id, 2, "active"),
            NewEnrollment(_user.Id, _class.Id, 4, "cancelled"));
        await db.SaveChangesAsync();
        Authenticate(_admin);
    }

    [TestCleanup]
    public void CleanUp()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task GetLoad_WithoutRequiredAuthorization_RejectsRequest()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.GetAsync(Url());
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Authenticate(_user);
        using var userResponse = await _client.GetAsync(Url());
        Assert.AreEqual(HttpStatusCode.Forbidden, userResponse.StatusCode);

        Authenticate(_admin);
        using var adminResponse = await _client.GetAsync(Url());
        Assert.AreEqual(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [TestMethod]
    public async Task GetLoad_InvalidParameters_ReturnProblemDetails()
    {
        foreach (var url in new[]
        {
            "/analytics/load?to=2035-01-01",
            "/analytics/load?from=2035-01-01",
            "/analytics/load?from=invalid&to=2035-01-01",
            "/analytics/load?from=2035-01-01T10:00:00Z&to=2035-01-01",
            "/analytics/load?from=2035-01-02&to=2035-01-01",
            "/analytics/load?from=2035-01-01&to=2035-01-01&roomId=bad"
        })
        {
            using var response = await _client.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, url);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(400, body.RootElement.GetProperty("status").GetInt32());
        }
    }

    [TestMethod]
    public async Task GetLoad_WithFilters_ReturnsGroupedAndSortedResults()
    {
        using var response = await _client.GetAsync(Url(_date.AddDays(1)));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(new[] { "items" }, json.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        var items = json.RootElement.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("room").GetProperty("id").GetInt64() == _room.Id ||
                        x.GetProperty("room").GetProperty("id").GetInt64() == _otherRoom.Id)
            .ToArray();
        Assert.AreEqual(4, items.Length);
        CollectionAssert.AreEqual(new[] { _room.Id, _otherRoom.Id, _room.Id, _otherRoom.Id },
            items.Select(x => x.GetProperty("room").GetProperty("id").GetInt64()).ToArray());
        CollectionAssert.AreEqual(new[] { _date.ToString("yyyy-MM-dd"), _date.ToString("yyyy-MM-dd"),
                _date.AddDays(1).ToString("yyyy-MM-dd"), _date.AddDays(1).ToString("yyyy-MM-dd") },
            items.Select(x => x.GetProperty("date").GetString()).ToArray());
        CollectionAssert.AreEquivalent(new[] { "date", "room", "classesCount", "totalCapacity", "bookedSeats" },
            items[0].EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "id", "name" }, items[0].GetProperty("room").EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(2, items[0].GetProperty("classesCount").GetInt32());
        Assert.AreEqual(30, items[0].GetProperty("totalCapacity").GetInt32());
        Assert.AreEqual(5, items[0].GetProperty("bookedSeats").GetInt32());

        using var filtered = await _client.GetAsync(Url() + $"&roomId={_room.Id}");
        var body = await filtered.Content.ReadFromJsonAsync<LoadResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(1, body.Items.Count);
        Assert.AreEqual(30, body.Items[0].TotalCapacity);

        using var absent = await _client.GetAsync(Url() + "&roomId=9223372036854775807");
        var empty = await absent.Content.ReadFromJsonAsync<LoadResponse>();
        Assert.AreEqual(HttpStatusCode.OK, absent.StatusCode);
        Assert.IsNotNull(empty);
        Assert.AreEqual(0, empty.Items.Count);
    }

    [DataTestMethod]
    [DataRow("", HttpStatusCode.OK)]
    [DataRow("&roomId", HttpStatusCode.BadRequest)]
    [DataRow("&roomId=", HttpStatusCode.BadRequest)]
    [DataRow("&roomId=%20", HttpStatusCode.BadRequest)]
    [DataRow("&roomId=%09%20", HttpStatusCode.BadRequest)]
    public async Task GetLoad_WithOptionalRoomId_ValidatesParameter(string suffix, HttpStatusCode expectedStatus)
    {
        using var response = await _client.GetAsync(Url() + suffix);

        Assert.AreEqual(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.BadRequest)
        {
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(400, body.RootElement.GetProperty("status").GetInt32());
            Assert.IsTrue(body.RootElement.GetProperty("errors").TryGetProperty("roomId", out _));
        }
    }

    [TestMethod]
    public async Task GetLoad_AfterEnrollmentChanges_RefreshesCachedResults()
    {
        var url = Url() + $"&roomId={_room.Id}";
        Assert.AreEqual(5, await GetSeatsAsync(url));
        long version;
        using (var scope = _factory.Services.CreateScope())
        {
            var versions = scope.ServiceProvider.GetRequiredService<ICardCacheVersionService>();
            version = await versions.GetGlobalAsync(CancellationToken.None);
        }
        var key = $"analytics:load:v3:{version}:{_date:yyyy-MM-dd}:{_date:yyyy-MM-dd}:{_room.Id}";
        var redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        Assert.IsTrue(await redis.KeyExistsAsync(key));
        Assert.IsTrue((await redis.KeyTimeToLiveAsync(key)) > TimeSpan.Zero);
        var cached = await redis.StringGetAsync(key);
        Assert.IsFalse(cached.IsNullOrEmpty);
        var cachedBody = JsonSerializer.Deserialize<LoadResponse>((string)cached!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsNotNull(cachedBody);
        Assert.AreEqual(5, cachedBody.Items.Single().BookedSeats);
        await redis.StringSetAsync(key,
            "{\"items\":[{\"date\":\"2035-01-01\",\"room\":{\"id\":1,\"name\":\"cached\"},\"classesCount\":1,\"totalCapacity\":99,\"bookedSeats\":42}]}",
            TimeSpan.FromMinutes(5));
        Assert.AreEqual(42, await GetSeatsAsync(url));
        await redis.KeyDeleteAsync(key);
        Assert.AreEqual(5, await GetSeatsAsync(url));

        Authenticate(_user);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { classId = _class.Id, seats = 1 });
        using var created = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var enrollment = await created.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        Assert.IsNotNull(enrollment);
        Authenticate(_admin);
        Assert.AreEqual(6, await GetSeatsAsync(url));
        Authenticate(_user);
        using var changed = await _client.PostAsJsonAsync($"/enrollments/{enrollment.Id}/seats", new { seats = 2 });
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        Authenticate(_admin);
        Assert.AreEqual(7, await GetSeatsAsync(url));
        Authenticate(_user);
        using var cancelled = await _client.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Authenticate(_admin);
        Assert.AreEqual(5, await GetSeatsAsync(url));
    }

    private async Task<int> GetSeatsAsync(string url)
    {
        using var response = await _client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<LoadResponse>();
        Assert.IsNotNull(body);
        return body.Items.Single().BookedSeats;
    }

    private string Url(DateOnly? to = null) => $"/analytics/load?from={_date:yyyy-MM-dd}&to={to ?? _date:yyyy-MM-dd}";
    private void Authenticate(User user) => _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
        "Bearer", _factory.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken);
    private static User NewUser(string role) => new() { Email = $"analytics-{Guid.NewGuid():N}@test.local", PasswordHash = "test", Role = role, CreatedAt = DateTime.UtcNow };
    private static Class NewClass(Room room, DateTime start, int capacity, bool cancelled = false) => new()
    { Title = "Analytics class", Room = room, StartsAt = start, DurationMinutes = 60, Capacity = capacity, IsCancelled = cancelled };
    private static Enrollment NewEnrollment(long userId, long classId, int seats, string status) => new()
    { UserId = userId, ClassId = classId, Seats = seats, Status = status, CreatedAt = DateTime.UtcNow };
}
