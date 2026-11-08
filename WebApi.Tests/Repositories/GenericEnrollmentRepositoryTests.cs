using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Repositories;

[TestClass]
public class GenericEnrollmentRepositoryTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private long _userId;
    private long _classId;
    private long _firstEnrollmentId;
    private long _secondEnrollmentId;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var user = new User
        {
            Email = $"generic-{Guid.NewGuid():N}@test.local",
            PasswordHash = "test",
            Role = "user",
            CreatedAt = DateTime.UtcNow
        };
        var classEntity = new Class
        {
            Title = "Generic repository test",
            Room = new Room { Name = "Test room" },
            StartsAt = DateTime.UtcNow.AddDays(1),
            DurationMinutes = 60,
            Capacity = 20,
            IsCancelled = false
        };
        db.Users.Add(user);
        db.Classes.Add(classEntity);
        await db.SaveChangesAsync();

        var first = new Enrollment
        {
            UserId = user.Id, ClassId = classEntity.Id, Seats = 1,
            Status = "active", CreatedAt = DateTime.UtcNow
        };
        var second = new Enrollment
        {
            UserId = user.Id, ClassId = classEntity.Id, Seats = 2,
            Status = "cancelled", CreatedAt = DateTime.UtcNow, CancelledAt = DateTime.UtcNow
        };
        db.Enrollments.AddRange(first, second);
        await db.SaveChangesAsync();

        _userId = user.Id;
        _classId = classEntity.Id;
        _firstEnrollmentId = first.Id;
        _secondEnrollmentId = second.Id;
    }

    [TestCleanup]
    public void CleanUp()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [TestMethod]
    public async Task Query_AdditionalFilter_ReturnsMatchingEnrollment()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new EnrollmentRepository(db.Enrollments);

        var result = await repository.Query()
            .Where(e => e.UserId == _userId && e.Seats == 2)
            .ToListAsync();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(_secondEnrollmentId, result[0].Id);
    }

    [TestMethod]
    public async Task GetByIdAsync_ExistingAndMissingIds_ReturnsExpectedResults()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new EnrollmentRepository(db.Enrollments);

        var existing = await repository.GetByIdAsync(_firstEnrollmentId);
        var missing = await repository.GetByIdAsync(long.MaxValue);

        Assert.IsNotNull(existing);
        Assert.AreEqual(_firstEnrollmentId, existing.Id);
        Assert.IsNull(missing);
    }

    [TestMethod]
    public async Task GetAllAsync_WithAndWithoutFilter_ReturnsExpectedEnrollments()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new EnrollmentRepository(db.Enrollments);

        var all = await repository.GetAllAsync();
        var filtered = await repository.GetAllAsync(q => q.Where(e => e.UserId == _userId && e.Status == "active"));

        Assert.IsTrue(all.Any(e => e.Id == _firstEnrollmentId));
        Assert.IsTrue(all.Any(e => e.Id == _secondEnrollmentId));
        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual(_firstEnrollmentId, filtered[0].Id);
    }

    [TestMethod]
    public async Task AnyAsync_WithAndWithoutFilter_ReturnsExpectedResult()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new EnrollmentRepository(db.Enrollments);

        Assert.IsTrue(await repository.AnyAsync());
        Assert.IsTrue(await repository.AnyAsync(q => q.Where(e => e.Id == _firstEnrollmentId)));
        Assert.IsFalse(await repository.AnyAsync(q => q.Where(e => e.Id == long.MaxValue)));
    }

    [TestMethod]
    public async Task CountAsync_WithAndWithoutFilter_ReturnsExpectedCount()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new EnrollmentRepository(db.Enrollments);

        var allCount = await repository.CountAsync();
        var userCount = await repository.CountAsync(q => q.Where(e => e.UserId == _userId));
        var activeCount = await repository.CountAsync(q => q.Where(e => e.UserId == _userId && e.Status == "active"));

        Assert.AreEqual(await db.Enrollments.CountAsync(), allCount);
        Assert.AreEqual(2, userCount);
        Assert.AreEqual(1, activeCount);
    }

    [TestMethod]
    public async Task CreateAsync_BeforeSaveChanges_DoesNotPersistEntity()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new EnrollmentRepository(db.Enrollments);
        var enrollment = new Enrollment
        {
            UserId = _userId, ClassId = _classId, Seats = 3,
            Status = "active", CreatedAt = DateTime.UtcNow
        };

        var result = await repository.CreateAsync(enrollment);
        Assert.AreSame(enrollment, result);
        Assert.AreEqual(EntityState.Added, db.Entry(enrollment).State);

        using var checkScope = _factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(2, await checkDb.Enrollments.CountAsync(e => e.UserId == _userId));

        await db.SaveChangesAsync();
        Assert.AreEqual(3, await checkDb.Enrollments.CountAsync(e => e.UserId == _userId));
        Assert.IsTrue(await checkDb.Enrollments.AnyAsync(e => e.Id == enrollment.Id));
    }

    [TestMethod]
    public async Task Update_BeforeSaveChanges_DoesNotPersistChanges()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new EnrollmentRepository(db.Enrollments);
        var enrollment = await repository.GetByIdAsync(_firstEnrollmentId);
        Assert.IsNotNull(enrollment);
        enrollment.Seats = 4;

        var result = repository.Update(enrollment);
        Assert.AreSame(enrollment, result);
        Assert.AreEqual(EntityState.Modified, db.Entry(enrollment).State);

        using var checkScope = _factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.AreEqual(1, await checkDb.Enrollments.Where(e => e.Id == _firstEnrollmentId).Select(e => e.Seats).SingleAsync());

        await db.SaveChangesAsync();
        Assert.AreEqual(4, await checkDb.Enrollments.Where(e => e.Id == _firstEnrollmentId).Select(e => e.Seats).SingleAsync());
    }

    [TestMethod]
    public async Task Delete_BeforeSaveChanges_DoesNotRemoveEntity()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new EnrollmentRepository(db.Enrollments);
        var enrollment = await repository.GetByIdAsync(_firstEnrollmentId);
        Assert.IsNotNull(enrollment);

        var result = repository.Delete(enrollment);
        Assert.AreSame(enrollment, result);
        Assert.AreEqual(EntityState.Deleted, db.Entry(enrollment).State);

        using var checkScope = _factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.IsTrue(await checkDb.Enrollments.AnyAsync(e => e.Id == _firstEnrollmentId));

        await db.SaveChangesAsync();
        Assert.IsFalse(await checkDb.Enrollments.AnyAsync(e => e.Id == _firstEnrollmentId));
    }
}
