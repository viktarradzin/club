using WebApi.Services.Caching;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Services;

[TestClass]
public class CardCacheVersionServiceTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private IServiceScope _scope = null!;
    private ClubDbContext _db = null!;
    private ICardCacheVersionService _service = null!;
    private long _userId;
    private long _otherUserId;

    [TestInitialize]
    public async Task SetUp()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        _scope = _factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        _service = _scope.ServiceProvider.GetRequiredService<ICardCacheVersionService>();
        var users = Enumerable.Range(0, 2).Select(_ => new User
        {
            Email = $"version-{Guid.NewGuid():N}@test.local", PasswordHash = "unused",
            Role = "user", CreatedAt = DateTime.UtcNow
        }).ToArray();
        _db.Users.AddRange(users);
        await _db.SaveChangesAsync();
        _userId = users[0].Id;
        _otherUserId = users[1].Id;
    }

    [TestCleanup]
    public void CleanUp()
    {
        _scope?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [TestMethod]
    public async Task GetAsync_BeforeFirstChange_ReturnsZeroWithoutCreatingRow()
    {
        Assert.AreEqual(0L, await _service.GetAsync(_userId, CancellationToken.None));
        Assert.IsFalse(await _db.EnrollmentCardCacheVersions.AnyAsync(x => x.Id == _userId));
    }

    [TestMethod]
    public async Task IncrementAsync_ChangesOnlyOwnerVersionAndAdvancesGlobalVersion()
    {
        var global = await _service.GetGlobalAsync(CancellationToken.None);
        await _service.IncrementAsync(_userId, CancellationToken.None);
        await _service.IncrementAsync(_userId, CancellationToken.None);

        Assert.AreEqual(2L, await _service.GetAsync(_userId, CancellationToken.None));
        Assert.AreEqual(0L, await _service.GetAsync(_otherUserId, CancellationToken.None));
        Assert.AreEqual(global + 2, await _service.GetGlobalAsync(CancellationToken.None));
        Assert.AreEqual(2L, await _db.EnrollmentCardCacheVersions.Where(x => x.Id == _userId)
            .Select(x => x.Version).SingleAsync());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IncrementAsync_InTransaction_RollsBackUserAndGlobalVersions(bool existingVersion)
    {
        if (existingVersion) await _service.IncrementAsync(_userId, CancellationToken.None);
        var original = await _service.GetAsync(_userId, CancellationToken.None);
        var global = await _service.GetGlobalAsync(CancellationToken.None);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _service.IncrementAsync(_userId, CancellationToken.None);
        Assert.AreEqual(original + 1, await _service.GetAsync(_userId, CancellationToken.None));
        Assert.AreEqual(global + 1, await _service.GetGlobalAsync(CancellationToken.None));
        await transaction.RollbackAsync();

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICardCacheVersionService>();
        Assert.AreEqual(original, await service.GetAsync(_userId, CancellationToken.None));
        Assert.AreEqual(global, await service.GetGlobalAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task IncrementAsync_WithAnotherUserTransactionOpen_DoesNotWaitForIt()
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _service.IncrementAsync(_userId, CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICardCacheVersionService>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.IncrementAsync(_otherUserId, cancellation.Token);
        Assert.AreEqual(1L, await service.GetAsync(_otherUserId, cancellation.Token));
        await transaction.RollbackAsync();
    }

    [TestMethod]
    public async Task ClassCancellation_RollsBackItsVersionAndDoesNotInvalidateUserCards()
    {
        var lesson = await AddClassAsync();
        var original = await _service.GetGlobalAsync(CancellationToken.None);
        await using var transaction = await _db.Database.BeginTransactionAsync();
        lesson.IsCancelled = true;
        await _db.SaveChangesAsync();

        Assert.AreEqual(original + 1, await _service.GetGlobalAsync(CancellationToken.None));
        Assert.AreEqual(0L, await _service.GetAsync(_userId, CancellationToken.None));
        using var scope = _factory.Services.CreateScope();
        var outside = scope.ServiceProvider.GetRequiredService<ICardCacheVersionService>();
        Assert.AreEqual(original, await outside.GetGlobalAsync(CancellationToken.None));

        await transaction.RollbackAsync();
        Assert.AreEqual(original, await outside.GetGlobalAsync(CancellationToken.None));
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.IsTrue(await db.Classes.AnyAsync(x => x.Id == lesson.Id));
    }

    [TestMethod]
    public async Task IncrementAsync_WithClassChangeTransactionOpen_DoesNotWaitForCatalogVersion()
    {
        var lesson = await AddClassAsync();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        lesson.IsCancelled = true;
        await _db.SaveChangesAsync();

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICardCacheVersionService>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.IncrementAsync(_otherUserId, cancellation.Token);
        Assert.AreEqual(1L, await service.GetAsync(_otherUserId, cancellation.Token));
        await transaction.RollbackAsync();
    }

    private async Task<Class> AddClassAsync()
    {
        var lesson = new Class
        {
            Title = "Version test", Room = new Room { Name = "Version test room" },
            StartsAt = DateTime.UtcNow.AddDays(3), Capacity = 10, DurationMinutes = 60
        };
        _db.Classes.Add(lesson);
        await _db.SaveChangesAsync();
        return lesson;
    }
}
