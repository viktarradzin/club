using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace WebApi.Tests.Infrastructure;

[TestClass]
public class ScheduleCacheVersionMigrationTests
{
    private const string PreviousMigration = "20261108070955_ScopeCardCacheVersionByUser";
    private ClubDbContext _db = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(TestDatabase.PostgresConnectionString)
        {
            Database = $"club_schedule_tests_{Guid.NewGuid():N}", Pooling = false
        }.ConnectionString;
        _db = new ClubDbContext(new DbContextOptionsBuilder<ClubDbContext>().UseNpgsql(connectionString).Options);
        await _db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
    }

    [TestCleanup]
    public async Task CleanUp()
    {
        if (_db is not null)
        {
            try { await _db.Database.EnsureDeletedAsync(); }
            finally { await _db.DisposeAsync(); }
        }
    }

    [TestMethod]
    public async Task Migration_PreservesClasses_InstallsTriggers_AndCanBeReapplied()
    {
        var lesson = new Class
        {
            Title = "Existing lesson", Room = new Room { Name = "Existing room" },
            StartsAt = new DateTime(2035, 1, 1, 10, 0, 0, DateTimeKind.Utc), Capacity = 10, DurationMinutes = 60
        };
        _db.Classes.Add(lesson);
        await _db.SaveChangesAsync();
        await _db.Database.MigrateAsync();

        Assert.AreEqual(0L, await VersionAsync());
        Assert.AreEqual(lesson.Id, await _db.Classes.Select(x => x.Id).SingleAsync());
        Assert.IsFalse(_db.Database.HasPendingModelChanges());
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE classes SET is_cancelled = true WHERE id = {lesson.Id}");
        Assert.AreEqual(1L, await VersionAsync());
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE rooms SET name = 'Renamed' WHERE id = {lesson.RoomId}");
        Assert.AreEqual(2L, await VersionAsync());

        await _db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE classes SET is_cancelled = false WHERE id = {lesson.Id}");
        Assert.AreEqual(1, await _db.Classes.CountAsync());
        await _db.Database.MigrateAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE classes SET is_cancelled = true WHERE id = {lesson.Id}");
        Assert.AreEqual(1L, await VersionAsync());
    }

    [TestMethod]
    public async Task Truncate_AdvancesVersion_AndRollsBackWithData()
    {
        await _db.Database.MigrateAsync();
        _db.Classes.Add(new Class
        {
            Title = "Truncate test lesson", Room = new Room { Name = "Truncate test room" },
            StartsAt = DateTime.UtcNow.AddDays(3), Capacity = 10, DurationMinutes = 60
        });
        await _db.SaveChangesAsync();
        var original = await VersionAsync();
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE classes CASCADE");
        Assert.AreEqual(original + 1, await VersionAsync());
        Assert.AreEqual(0, await _db.Classes.CountAsync());
        await transaction.RollbackAsync();
        Assert.AreEqual(original, await VersionAsync());
        Assert.AreEqual(1, await _db.Classes.CountAsync());
        Assert.AreEqual(1, await _db.Rooms.CountAsync());
    }

    private Task<long> VersionAsync() =>
        _db.Set<ScheduleCacheVersion>().Select(x => x.Version).SingleAsync();
}
