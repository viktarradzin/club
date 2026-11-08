using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace WebApi.Tests.Infrastructure;

[TestClass]
public class RefundHistoryTests
{
    private ClubDbContext _db = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(TestDatabase.PostgresConnectionString)
        {
            Database = $"club_refund_tests_{Guid.NewGuid():N}",
            Pooling = false
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        _db = new ClubDbContext(options);
        await _db.GetService<IMigrator>().MigrateAsync("20261106153809_AddWaitlistEntries");
    }

    [TestCleanup]
    public async Task CleanUpAsync()
    {
        if (_db is not null)
        {
            try
            {
                await _db.Database.EnsureDeletedAsync();
            }
            finally
            {
                await _db.DisposeAsync();
            }
        }
    }

    [DataTestMethod]
    [DataRow(false, 180, null, 2)]
    [DataRow(false, 120, null, 2)]
    [DataRow(false, 119, null, 0)]
    [DataRow(true, -30, null, 2)]
    [DataRow(false, null, null, 0)]
    [DataRow(true, null, null, 2)]
    [DataRow(true, 30, 0, 0)]
    [DataRow(false, 30, 2, 2)]
    public async Task Migration_WithMissingCancelledRefunds_BackfillsWithoutChangingBalances(
        bool classCancelled, int? minutesBeforeStart, int? storedRefund, int expectedRefund)
    {
        var startsAt = new DateTime(2035, 1, 15, 18, 0, 0, DateTimeKind.Utc);
        var user = new User
        {
            Email = "refund-history@test.local", PasswordHash = "test",
            Role = "user", CreatedAt = startsAt.AddDays(-30)
        };
        var classEntity = new Class
        {
            Title = "Refund history", Room = new Room { Name = "Refund history room" },
            StartsAt = startsAt, DurationMinutes = 60, Capacity = 10, IsCancelled = classCancelled
        };
        _db.AddRange(user, classEntity);
        await _db.SaveChangesAsync();

        var cancelled = new Enrollment
        {
            UserId = user.Id, ClassId = classEntity.Id, Seats = 2, Status = "cancelled",
            CreatedAt = startsAt.AddDays(-2), RefundedVisits = storedRefund,
            CancelledAt = minutesBeforeStart.HasValue ? startsAt.AddMinutes(-minutesBeforeStart.Value) : null
        };
        var active = new Enrollment
        {
            UserId = user.Id, ClassId = classEntity.Id, Seats = 1, Status = "active",
            CreatedAt = startsAt.AddDays(-1)
        };
        _db.Enrollments.AddRange(cancelled, active);
        _db.Passes.Add(new Pass
        {
            UserId = user.Id, RemainingVisits = 7, ValidUntil = new DateOnly(2035, 2, 1)
        });
        await _db.SaveChangesAsync();
        var originalState = await ReadEnrollmentStateAsync();

        await _db.Database.MigrateAsync();
        _db.ChangeTracker.Clear();

        Assert.AreEqual(expectedRefund, (await _db.Enrollments.SingleAsync(x => x.Id == cancelled.Id)).RefundedVisits);
        Assert.IsNull((await _db.Enrollments.SingleAsync(x => x.Id == active.Id)).RefundedVisits);
        Assert.AreEqual(originalState, await ReadEnrollmentStateAsync());
        var pass = await _db.Passes.SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(7, pass.RemainingVisits);
        Assert.AreEqual(new DateOnly(2035, 2, 1), pass.ValidUntil);

        await _db.Database.MigrateAsync();
        Assert.AreEqual(expectedRefund, (await _db.Enrollments.AsNoTracking()
            .SingleAsync(x => x.Id == cancelled.Id)).RefundedVisits);
        Assert.AreEqual(7, (await _db.Passes.AsNoTracking().SingleAsync(x => x.UserId == user.Id)).RemainingVisits);
    }

    [TestMethod]
    public async Task Seeder_OnRepeatedRun_PreservesDataAndPopulatesCancelledRefunds()
    {
        await _db.Database.MigrateAsync();
        await DatabaseSeeder.SeedAsync(_db);
        _db.ChangeTracker.Clear();

        Assert.AreEqual(205, await _db.Users.CountAsync());
        Assert.AreEqual(205, await _db.Passes.CountAsync());
        Assert.AreEqual(10, await _db.Rooms.CountAsync());
        Assert.AreEqual(200, await _db.Classes.IgnoreQueryFilters().CountAsync());
        var cancelled = await _db.Enrollments.IgnoreQueryFilters().Include(x => x.Class)
            .Where(x => x.Status == "cancelled").ToListAsync();
        Assert.AreEqual(50, cancelled.Count);
        foreach (var enrollment in cancelled)
        {
            Assert.IsNotNull(enrollment.CancelledAt);
            var expectedRefund = enrollment.Class.IsCancelled ||
                                 enrollment.Class.StartsAt - enrollment.CancelledAt.Value >= TimeSpan.FromHours(2)
                ? enrollment.Seats
                : 0;
            Assert.AreEqual(expectedRefund, enrollment.RefundedVisits, $"Enrollment {enrollment.Id}");
        }
        Assert.IsFalse(await _db.Enrollments.AnyAsync(x => x.Status == "active" && x.RefundedVisits != null));
        var originalEnrollments = await ReadEnrollmentStateAsync();
        var originalRefunds = await _db.Enrollments.OrderBy(x => x.Id).Select(x => x.RefundedVisits).ToArrayAsync();
        var originalPasses = await _db.Passes.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();

        await DatabaseSeeder.SeedAsync(_db);
        _db.ChangeTracker.Clear();

        Assert.AreEqual(originalEnrollments, await ReadEnrollmentStateAsync());
        CollectionAssert.AreEqual(originalRefunds, await _db.Enrollments.OrderBy(x => x.Id)
            .Select(x => x.RefundedVisits).ToArrayAsync());
        var passes = await _db.Passes.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
        Assert.AreEqual(JsonSerializer.Serialize(originalPasses), JsonSerializer.Serialize(passes));
        Assert.IsFalse(_db.Database.HasPendingModelChanges());
    }

    private async Task<string> ReadEnrollmentStateAsync()
    {
        return JsonSerializer.Serialize(await _db.Enrollments.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.UserId, x.ClassId, x.Seats, x.Status, x.CreatedAt, x.CancelledAt })
            .ToArrayAsync());
    }
}
