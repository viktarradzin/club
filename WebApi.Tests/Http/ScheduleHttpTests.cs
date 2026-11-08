using WebApi.Contracts.Enrollments.Create;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Contracts.Schedule;
using WebApi.Services.Caching;
using WebApi.Services.Schedule;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Http;

[TestClass]
public class ScheduleHttpTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private User _user = null!;
    private User _other = null!;
    private Class[] _classes = null!;
    private Room _room = null!;
    private DateTime _from;
    private DateTime _to;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        _from = DateTime.UtcNow.Date.AddDays(7);
        _to = _from.AddDays(1);
        _user = NewUser();
        _other = NewUser();
        _room = new Room { Name = "Schedule test room" };
        _classes = Enumerable.Range(0, 3).Select(i => new Class
        {
            Title = $"Schedule {i}", Room = _room, StartsAt = _from.AddHours(10),
            Capacity = 10, DurationMinutes = 60
        }).ToArray();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        db.Users.AddRange(_user, _other);
        db.Classes.AddRange(_classes);
        db.Classes.Add(new Class
        {
            Title = "Cancelled", Room = _room, StartsAt = _from.AddHours(10),
            Capacity = 10, DurationMinutes = 60, IsCancelled = true
        });
        db.Classes.Add(new Class
        {
            Title = "Excluded end", Room = _room, StartsAt = _to,
            Capacity = 10, DurationMinutes = 60
        });
        await db.SaveChangesAsync();
        db.Passes.AddRange(new Pass
        {
            UserId = _user.Id, RemainingVisits = 20, ValidUntil = DateOnly.FromDateTime(_to.AddDays(30))
        }, new Pass
        {
            UserId = _other.Id, RemainingVisits = 20, ValidUntil = DateOnly.FromDateTime(_to.AddDays(30))
        });
        db.Enrollments.AddRange(
            Enrollment(_user.Id, 1, "active"), Enrollment(_user.Id, 2, "active"),
            Enrollment(_other.Id, 2, "active"), Enrollment(_user.Id, 3, "cancelled"));
        await db.SaveChangesAsync();
        Authenticate(_user);
    }

    [TestCleanup]
    public void CleanUp()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task Repository_WithPeriodAndRoom_AggregatesActiveSeats()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new ClassRepository(db.Classes);
        var rows = await repository.GetSchedulePageAsync(new SchedulePageQuery
        {
            From = _from, To = _to, RoomId = _room.Id, UserId = _user.Id, Take = 10
        });

        CollectionAssert.AreEqual(_classes.Select(x => x.Id).ToArray(), rows.Select(x => x.ClassId).ToArray());
        Assert.AreEqual(5L, rows[0].OccupiedSeats);
        Assert.AreEqual(3L, rows[0].MySeats);
        Assert.AreEqual(0L, rows[1].OccupiedSeats);
        Assert.AreEqual(_room.Name, rows[0].RoomName);
    }

    [TestMethod]
    public async Task GetSchedule_WithEqualStartTimes_ReturnsPagesWithoutDuplicates()
    {
        var first = await GetScheduleAsync("&limit=2");
        Assert.AreEqual(2, first.Items.Count);
        Assert.IsFalse(string.IsNullOrEmpty(first.ContinuationToken));

        var second = await GetScheduleAsync("&limit=2&continuationToken=" + Uri.EscapeDataString(first.ContinuationToken!));
        Assert.AreEqual(1, second.Items.Count);
        Assert.IsNull(second.ContinuationToken);
        CollectionAssert.AreEqual(_classes.Select(x => x.Id).ToArray(),
            first.Items.Concat(second.Items).Select(x => x.ClassId).ToArray());
        Assert.AreEqual(5, first.Items[0].FreeSeats);
        Assert.AreEqual(3, first.Items[0].MySeats);
    }

    [TestMethod]
    public async Task GetSchedule_WithAbsentOrEmptyToken_ReturnsFirstPageOrBadRequest()
    {
        using var first = await _client.GetAsync(Url());
        using var empty = await _client.GetAsync(Url() + "&continuationToken=");
        using var whitespace = await _client.GetAsync(Url() + "&continuationToken=%20");

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, whitespace.StatusCode);
    }

    [TestMethod]
    public async Task GetSchedule_WithInvalidParametersOrAnonymousUser_RejectsRequest()
    {
        foreach (var url in new[]
        {
            "/schedule?to=2030-01-01T00:00:00Z",
            "/schedule?from=bad&to=2030-01-01T00:00:00Z",
            "/schedule?from=2030-01-01T00:00:00Z&to=2030-01-01T00:00:00Z",
            Url() + "&limit=0", Url() + "&limit=101", Url() + "&continuationToken=invalid!",
            $"/schedule?from={Uri.EscapeDataString(_from.ToString("O"))}&to={Uri.EscapeDataString(_to.ToString("O"))}&roomId=invalid"
        })
        {
            using var response = await _client.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, url);
        }

        _client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await _client.GetAsync(Url());
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [DataTestMethod]
    [DataRow("", HttpStatusCode.OK)]
    [DataRow("&roomId", HttpStatusCode.BadRequest)]
    [DataRow("&roomId=", HttpStatusCode.BadRequest)]
    [DataRow("&roomId=%20", HttpStatusCode.BadRequest)]
    [DataRow("&roomId=%09%20", HttpStatusCode.BadRequest)]
    public async Task GetSchedule_WithOptionalRoomId_ValidatesParameter(string suffix, HttpStatusCode expectedStatus)
    {
        using var response = await _client.GetAsync(Url(includeRoomId: false) + suffix);

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
    public async Task GetSchedule_UnknownRoomId_ReturnsEmptySchedule()
    {
        using var response = await _client.GetAsync(Url(includeRoomId: false) + "&roomId=9223372036854775807");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(0, body.Items.Count);
        Assert.IsNull(body.ContinuationToken);
    }

    [TestMethod]
    public async Task GetSchedule_AfterEnrollmentChanges_RefreshesUserSpecificCache()
    {
        var initial = await GetScheduleAsync();
        AssertSeats(initial, 5, 3);
        using (var scope = _factory.Services.CreateScope())
        {
            var versions = scope.ServiceProvider.GetRequiredService<ICardCacheVersionService>();
            var version = await versions.GetGlobalAsync(CancellationToken.None);
            var key = ScheduleCacheKey.Create(version, new SchedulePageQuery
            {
                From = _from, To = _to, RoomId = _room.Id, UserId = _user.Id, Take = 21
            });
            var redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
            Assert.IsTrue(await redis.KeyExistsAsync(key));
        }

        Authenticate(_other);
        AssertSeats(await GetScheduleAsync(), 5, 2);
        Authenticate(_user);
        AssertSeats(await GetScheduleAsync(), 5, 3);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments");
        createRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        createRequest.Content = JsonContent.Create(new { classId = _classes[0].Id, seats = 1 });
        using var create = await _client.SendAsync(createRequest);
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        Assert.IsNotNull(created);
        AssertSeats(await GetScheduleAsync(), 4, 4);

        using var change = await _client.PostAsJsonAsync($"/enrollments/{created.Id}/seats", new { seats = 2 });
        Assert.AreEqual(HttpStatusCode.OK, change.StatusCode);
        AssertSeats(await GetScheduleAsync(), 3, 5);

        using var cancel = await _client.PostAsync($"/enrollments/{created.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        AssertSeats(await GetScheduleAsync(), 5, 3);
        Authenticate(_other);
        AssertSeats(await GetScheduleAsync(), 5, 2);
    }

    [DataTestMethod]
    [DataRow(1999, 1, 1_000_000, 0)]
    [DataRow(2035, 1, 1_000_000, 0)]
    [DataRow(1999, -1_000_000, 1, 3)]
    [DataRow(2035, -1_000_000, 1, 3)]
    [DataRow(2035, -1, 1, 3)]
    [DataRow(2035, 1, 9, 0)]
    [DataRow(2035, -10, 0, 0)]
    [DataRow(2035, 0, 10, 3)]
    public async Task GetSchedule_WithSubMicrosecondBounds_PreservesExactPeriod(
        int year, int fromTicks, int toTicks, int expectedCount)
    {
        var start = new DateTime(year, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var ids = _classes.Select(x => x.Id).ToArray();
            await db.Classes.Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.StartsAt, start));
        }

        using var response = await _client.GetAsync(Url(from: start.AddTicks(fromTicks), to: start.AddTicks(toTicks)));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(expectedCount, body.Items.Count);
        Assert.IsTrue(body.Items.All(x => x.StartsAt >= start.AddTicks(fromTicks) && x.StartsAt < start.AddTicks(toTicks)));
    }

    [TestMethod]
    public async Task GetSchedule_WithMaximumDate_DoesNotOverflow()
    {
        using var response = await _client.GetAsync(Url(to: DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.IsNotNull(body);
        Assert.IsTrue(_classes.All(expected => body.Items.Any(x => x.ClassId == expected.Id)));
    }

    [TestMethod]
    public async Task GetSchedule_WithSubMicrosecondFrom_PaginatesWithItsOwnTokens()
    {
        var start = _classes[0].StartsAt;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            await db.Classes.Where(x => x.Id == _classes[1].Id || x.Id == _classes[2].Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.StartsAt, start.AddTicks(10)));
        }

        var url = Url(from: start.AddTicks(1)) + "&limit=1";
        using var firstResponse = await _client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.IsNotNull(first);
        Assert.AreEqual(_classes[1].Id, first.Items.Single().ClassId);
        Assert.IsNotNull(first.ContinuationToken);

        using var secondResponse = await _client.GetAsync(url + "&continuationToken=" + Uri.EscapeDataString(first.ContinuationToken));
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode, await secondResponse.Content.ReadAsStringAsync());
        var second = await secondResponse.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.IsNotNull(second);
        Assert.AreEqual(_classes[2].Id, second.Items.Single().ClassId);
        Assert.IsNull(second.ContinuationToken);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetSchedule_AfterClubCancelsClass_RefreshesCachedPages(bool useSql)
    {
        Assert.IsTrue((await GetScheduleAsync()).Items.Any(x => x.ClassId == _classes[0].Id));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            if (useSql)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE classes SET is_cancelled = true WHERE id = {_classes[0].Id}");
            }
            else
            {
                var lesson = await db.Classes.SingleAsync(x => x.Id == _classes[0].Id);
                lesson.IsCancelled = true;
                await db.SaveChangesAsync();
            }
        }

        var after = await GetScheduleAsync();
        CollectionAssert.AreEqual(_classes.Skip(1).Select(x => x.Id).ToArray(), after.Items.Select(x => x.ClassId).ToArray());
    }

    [TestMethod]
    public async Task GetSchedule_AfterClassAndRoomChanges_RefreshesCachedFields()
    {
        await GetScheduleAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            await db.Classes.Where(x => x.Id == _classes[0].Id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Title, "Updated class")
                .SetProperty(x => x.Capacity, 12));
        }
        var changed = (await GetScheduleAsync()).Items.Single(x => x.ClassId == _classes[0].Id);
        Assert.AreEqual("Updated class", changed.Title);
        Assert.AreEqual(12, changed.Capacity);
        Assert.AreEqual(7, changed.FreeSeats);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            await db.Rooms.Where(x => x.Id == _room.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Name, "Renamed room"));
        }
        Assert.IsTrue((await GetScheduleAsync()).Items.All(x => x.Room.Name == "Renamed room"));
    }

    [TestMethod]
    public async Task GetSchedule_AfterInsertionAndDeletion_RefreshesCachedPageBoundary()
    {
        Assert.AreEqual(2, (await GetScheduleAsync("&limit=2")).Items.Count);
        var added = new Class
        {
            RoomId = _room.Id, Title = "Added before first page", StartsAt = _from,
            Capacity = 10, DurationMinutes = 60
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            db.Classes.Add(added);
            await db.SaveChangesAsync();
        }
        Assert.AreEqual(added.Id, (await GetScheduleAsync("&limit=2")).Items[0].ClassId);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            await db.Classes.Where(x => x.Id == added.Id).ExecuteDeleteAsync();
        }
        Assert.AreEqual(_classes[0].Id, (await GetScheduleAsync("&limit=2")).Items[0].ClassId);
    }

    private Enrollment Enrollment(long userId, int seats, string status) => new()
    {
        UserId = userId, ClassId = _classes[0].Id, Seats = seats, Status = status,
        CreatedAt = DateTime.UtcNow
    };

    private static User NewUser() => new()
    {
        Email = $"schedule-{Guid.NewGuid():N}@test.local", PasswordHash = "test",
        Role = "user", CreatedAt = DateTime.UtcNow
    };

    private void Authenticate(User user)
    {
        var token = _factory.Services.GetRequiredService<AccessTokenService>().Create(user).AccessToken;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private string Url(bool includeRoomId = true, DateTime? from = null, DateTime? to = null) =>
        $"/schedule?from={Uri.EscapeDataString((from ?? _from).ToString("O"))}&to={Uri.EscapeDataString((to ?? _to).ToString("O"))}" +
        (includeRoomId ? $"&roomId={_room.Id}" : "");

    private async Task<ScheduleResponse> GetScheduleAsync(string suffix = "")
    {
        using var response = await _client.GetAsync(Url() + suffix);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<ScheduleResponse>();
        Assert.IsNotNull(body);
        return body;
    }

    private void AssertSeats(ScheduleResponse response, int free, int mine)
    {
        var item = response.Items.Single(x => x.ClassId == _classes[0].Id);
        Assert.AreEqual(free, item.FreeSeats);
        Assert.AreEqual(mine, item.MySeats);
    }
}
