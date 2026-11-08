using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace WebApi.Tests.Infrastructure;

[TestClass]
public class ScheduleIndexesMigrationTests
{
    private const string PreviousMigration = "20261108114026_AddScheduleCacheVersion";
    private ClubDbContext _db = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(TestDatabase.PostgresConnectionString)
        {
            Database = $"club_schedule_indexes_tests_{Guid.NewGuid():N}", Pooling = false
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
    public async Task Migration_OnSeededDatabase_AddsPartialIndexes_PreservesData_AndCanBeReapplied()
    {
        await DatabaseSeeder.SeedAsync(_db);
        var originalData = await ReadDataAsync();
        var originalIndexes = await ReadIndexesAsync();
        Assert.IsFalse(originalIndexes.ContainsKey("IX_classes_starts_at_id"));
        Assert.IsFalse(originalIndexes.ContainsKey("IX_classes_room_id_starts_at_id"));
        Assert.IsTrue(originalIndexes.ContainsKey("IX_classes_room_id"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _db.Database.MigrateAsync();
            Assert.IsFalse(_db.Database.HasPendingModelChanges());
            Assert.AreEqual(originalData, await ReadDataAsync());
            var indexes = await ReadIndexesAsync();
            Assert.AreEqual(originalIndexes.Count + 2, indexes.Count);
            StringAssert.Contains(indexes["IX_classes_starts_at_id"], "(starts_at, id)");
            StringAssert.Contains(indexes["IX_classes_starts_at_id"], "WHERE (NOT is_cancelled)");
            StringAssert.Contains(indexes["IX_classes_room_id_starts_at_id"], "(room_id, starts_at, id)");
            StringAssert.Contains(indexes["IX_classes_room_id_starts_at_id"], "WHERE (NOT is_cancelled)");
            foreach (var index in originalIndexes)
            {
                Assert.AreEqual(index.Value, indexes[index.Key]);
            }

            await _db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.AreEqual(originalData, await ReadDataAsync());
            var revertedIndexes = await ReadIndexesAsync();
            CollectionAssert.AreEquivalent(originalIndexes.Keys.ToArray(), revertedIndexes.Keys.ToArray());
            foreach (var index in originalIndexes)
            {
                Assert.AreEqual(index.Value, revertedIndexes[index.Key]);
            }
        }
    }

    private async Task<Dictionary<string, string>> ReadIndexesAsync()
    {
        await using var connection = new NpgsqlConnection(_db.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'classes'
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new Dictionary<string, string>();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0), reader.GetString(1));
        }
        return indexes;
    }

    private async Task<string> ReadDataAsync()
    {
        return JsonSerializer.Serialize(new
        {
            Classes = await _db.Classes.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.RoomId, x.Title, x.StartsAt, x.DurationMinutes, x.Capacity, x.IsCancelled })
                .ToArrayAsync(),
            Enrollments = await _db.Enrollments.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.UserId, x.ClassId, x.Seats, x.Status, x.CreatedAt, x.CancelledAt, x.RefundedVisits })
                .ToArrayAsync(),
            Passes = await _db.Passes.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
        });
    }
}
