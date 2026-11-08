using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Seeding;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Services.Caching;

namespace WebApi.Tests.Infrastructure;

[TestClass]
public class CardCacheVersionMigrationTests
{
    private const string PreviousMigration = "20261107142733_BackfillEnrollmentRefundedVisits";
    private ClubDbContext _db = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(TestDatabase.PostgresConnectionString)
        {
            Database = $"club_version_tests_{Guid.NewGuid():N}", Pooling = false
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
    public async Task Migration_BackfillsAllUsersAndPreservesBalancesAndBookings()
    {
        var users = Enumerable.Range(0, 2).Select(_ => new User
        {
            Email = $"migration-{Guid.NewGuid():N}@test.local", PasswordHash = "unused",
            Role = "user", CreatedAt = DateTime.UtcNow
        }).ToArray();
        var lesson = new Class
        {
            Title = "Migration", Room = new Room { Name = "Migration room" }, StartsAt = DateTime.UtcNow.AddDays(3),
            DurationMinutes = 60, Capacity = 10
        };
        _db.Users.AddRange(users);
        _db.Classes.Add(lesson);
        await _db.SaveChangesAsync();
        _db.Passes.AddRange(users.Select(user => new Pass
        {
            UserId = user.Id, RemainingVisits = 7, ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
        }));
        var enrollment = new Enrollment
        {
            UserId = users[0].Id, ClassId = lesson.Id, Seats = 2, Status = "active", CreatedAt = DateTime.UtcNow
        };
        _db.Enrollments.Add(enrollment);
        await _db.SaveChangesAsync();
        await _db.EnrollmentCardCacheVersions.ExecuteUpdateAsync(x => x.SetProperty(v => v.Version, 123L));

        await _db.Database.MigrateAsync();
        _db.ChangeTracker.Clear();

        CollectionAssert.AreEquivalent(users.Select(x => x.Id).ToArray(),
            await _db.EnrollmentCardCacheVersions.Select(x => x.Id).ToArrayAsync());
        Assert.IsTrue(await _db.EnrollmentCardCacheVersions.AllAsync(x => x.Version == 0));
        Assert.AreEqual(2, await _db.Users.CountAsync());
        Assert.AreEqual(14, await _db.Passes.SumAsync(x => x.RemainingVisits));
        Assert.AreEqual(2, (await _db.Enrollments.SingleAsync(x => x.Id == enrollment.Id)).Seats);
        Assert.IsFalse(_db.Database.HasPendingModelChanges());

        await new CardCacheVersionService(new UnitOfWork(_db)).IncrementAsync(users[1].Id, CancellationToken.None);
        await _db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        Assert.AreEqual(1, await _db.EnrollmentCardCacheVersions.CountAsync());
        Assert.AreEqual(1L, (await _db.EnrollmentCardCacheVersions.SingleAsync()).Id);
        await _db.Database.MigrateAsync();
        Assert.AreEqual(2, await _db.EnrollmentCardCacheVersions.CountAsync());
        Assert.AreEqual(14, await _db.Passes.SumAsync(x => x.RemainingVisits));
    }

    [TestMethod]
    public async Task FreshDatabase_SeederCreatesOneInitialVersionPerUser()
    {
        await _db.Database.MigrateAsync();
        await DatabaseSeeder.SeedAsync(_db);
        Assert.AreEqual(205, await _db.Users.CountAsync());
        Assert.AreEqual(205, await _db.EnrollmentCardCacheVersions.CountAsync());
        Assert.IsTrue(await _db.EnrollmentCardCacheVersions.AllAsync(x => x.Version == 0));
        await DatabaseSeeder.SeedAsync(_db);
        Assert.AreEqual(205, await _db.EnrollmentCardCacheVersions.CountAsync());
    }
}
