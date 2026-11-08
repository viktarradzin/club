using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Repositories;

[TestClass]
public class EnrollmentRepositoryTests
{
    [TestMethod]
    public void Constructor_WhenDbSetIsNull_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => new EnrollmentRepository(null!));
    }

    [TestMethod]
    public async Task GetByUserAsync_WithMultipleUsers_ReturnsOnlySpecifiedUsersEnrollments()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();

        long firstUserId;
        long expectedEnrollmentId;

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider
                .GetRequiredService<ClubDbContext>();

            var firstUser = new User
            {
                Email = $"first-{Guid.NewGuid()}@test.local",
                PasswordHash = "test",
                Role = "user",
                CreatedAt = DateTime.UtcNow
            };

            var secondUser = new User
            {
                Email = $"second-{Guid.NewGuid()}@test.local",
                PasswordHash = "test",
                Role = "user",
                CreatedAt = DateTime.UtcNow
            };

            var room = new Room
            {
                Name = "Test room"
            };

            var classEntity = new Class
            {
                Title = "Test class",
                Room = room,
                StartsAt = DateTime.UtcNow.AddDays(1),
                DurationMinutes = 60,
                Capacity = 10,
                IsCancelled = false
            };

            db.Users.AddRange(firstUser, secondUser);
            db.Classes.Add(classEntity);
            await db.SaveChangesAsync();

            var firstEnrollment = new Enrollment
            {
                UserId = firstUser.Id,
                ClassId = classEntity.Id,
                Seats = 1,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };

            var secondEnrollment = new Enrollment
            {
                UserId = secondUser.Id,
                ClassId = classEntity.Id,
                Seats = 2,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };

            db.Enrollments.AddRange(firstEnrollment, secondEnrollment);
            await db.SaveChangesAsync();

            firstUserId = firstUser.Id;
            expectedEnrollmentId = firstEnrollment.Id;
        }

        using var testScope = factory.Services.CreateScope();

        var testDb = testScope.ServiceProvider
            .GetRequiredService<ClubDbContext>();

        var repository = new EnrollmentRepository(testDb.Enrollments);

        var result = await repository.GetByUserAsync(firstUserId);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(expectedEnrollmentId, result[0].Id);
        Assert.AreEqual(firstUserId, result[0].UserId);
    }

    [TestMethod]
    public async Task GetOccupiedSeatsAsync_WithMultipleEnrollments_CountsActiveSeatsForSpecifiedClass()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();

        long firstClassId;

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider
                .GetRequiredService<ClubDbContext>();

            var user = new User
            {
                Email = $"user-{Guid.NewGuid()}@test.local",
                PasswordHash = "test",
                Role = "user",
                CreatedAt = DateTime.UtcNow
            };

            var room = new Room
            {
                Name = "Test room"
            };

            var firstClass = new Class
            {
                Title = "First class",
                Room = room,
                StartsAt = DateTime.UtcNow.AddDays(1),
                DurationMinutes = 60,
                Capacity = 20,
                IsCancelled = false
            };

            var secondClass = new Class
            {
                Title = "Second class",
                Room = room,
                StartsAt = DateTime.UtcNow.AddDays(1),
                DurationMinutes = 60,
                Capacity = 20,
                IsCancelled = false
            };

            db.Users.Add(user);
            db.Classes.AddRange(firstClass, secondClass);

            await db.SaveChangesAsync();

            var firstActiveEnrollment = new Enrollment
            {
                UserId = user.Id,
                ClassId = firstClass.Id,
                Seats = 2,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };

            var secondActiveEnrollment = new Enrollment
            {
                UserId = user.Id,
                ClassId = firstClass.Id,
                Seats = 3,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };

            var cancelledEnrollment = new Enrollment
            {
                UserId = user.Id,
                ClassId = firstClass.Id,
                Seats = 4,
                Status = "cancelled",
                CreatedAt = DateTime.UtcNow,
                CancelledAt = DateTime.UtcNow
            };

            var otherClassEnrollment = new Enrollment
            {
                UserId = user.Id,
                ClassId = secondClass.Id,
                Seats = 7,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };

            db.Enrollments.AddRange(
                firstActiveEnrollment,
                secondActiveEnrollment,
                cancelledEnrollment,
                otherClassEnrollment);

            await db.SaveChangesAsync();

            firstClassId = firstClass.Id;
        }

        using var testScope = factory.Services.CreateScope();

        var testDb = testScope.ServiceProvider
            .GetRequiredService<ClubDbContext>();

        var repository = new EnrollmentRepository(testDb.Enrollments);

        var occupiedSeats = await repository.GetOccupiedSeatsAsync(firstClassId);

        Assert.AreEqual(5L, occupiedSeats);
    }

    [TestMethod]
    public async Task GetByIdForUserWithDetailsAsync_WhenEnrollmentExists_ReturnsEnrollmentWithClassAndRoom()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();

        long userId;
        long enrollmentId;

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider
                .GetRequiredService<ClubDbContext>();

            var user = new User
            {
                Email = $"user-{Guid.NewGuid()}@test.local",
                PasswordHash = "test",
                Role = "user",
                CreatedAt = DateTime.UtcNow
            };

            var room = new Room
            {
                Name = "Test room"
            };

            var classEntity = new Class
            {
                Title = "Test class",
                Room = room,
                StartsAt = DateTime.UtcNow.AddDays(1),
                DurationMinutes = 60,
                Capacity = 10,
                IsCancelled = true
            };

            db.Users.Add(user);
            db.Classes.Add(classEntity);

            await db.SaveChangesAsync();

            var enrollment = new Enrollment
            {
                UserId = user.Id,
                ClassId = classEntity.Id,
                Seats = 2,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };

            db.Enrollments.Add(enrollment);
            await db.SaveChangesAsync();

            userId = user.Id;
            enrollmentId = enrollment.Id;
        }

        using var testScope = factory.Services.CreateScope();

        var testDb = testScope.ServiceProvider
            .GetRequiredService<ClubDbContext>();

        var repository = new EnrollmentRepository(testDb.Enrollments);

        var result = await repository.GetByIdForUserWithDetailsAsync(
            enrollmentId,
            userId);

        Assert.IsNotNull(result);
        Assert.AreEqual(enrollmentId, result.Id);
        Assert.AreEqual(userId, result.UserId);

        Assert.IsNotNull(result.Class);
        Assert.AreEqual("Test class", result.Class.Title);
        Assert.IsTrue(result.Class.IsCancelled);

        Assert.IsNotNull(result.Class.Room);
        Assert.AreEqual("Test room", result.Class.Room.Name);
    }

    [TestMethod]
    public async Task GetByIdForUserWithDetailsAsync_WhenUserIsNotOwner_ReturnsNull()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();

        long enrollmentId;
        long otherUserId;

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider
                .GetRequiredService<ClubDbContext>();

            var owner = new User
            {
                Email = $"owner-{Guid.NewGuid()}@test.local",
                PasswordHash = "test",
                Role = "user",
                CreatedAt = DateTime.UtcNow
            };

            var otherUser = new User
            {
                Email = $"other-{Guid.NewGuid()}@test.local",
                PasswordHash = "test",
                Role = "user",
                CreatedAt = DateTime.UtcNow
            };

            var room = new Room
            {
                Name = "Test room"
            };

            var classEntity = new Class
            {
                Title = "Test class",
                Room = room,
                StartsAt = DateTime.UtcNow.AddDays(1),
                DurationMinutes = 60,
                Capacity = 10,
                IsCancelled = false
            };

            db.Users.AddRange(owner, otherUser);
            db.Classes.Add(classEntity);
            await db.SaveChangesAsync();

            var enrollment = new Enrollment
            {
                UserId = owner.Id,
                ClassId = classEntity.Id,
                Seats = 1,
                Status = "active",
                CreatedAt = DateTime.UtcNow
            };

            db.Enrollments.Add(enrollment);
            await db.SaveChangesAsync();

            enrollmentId = enrollment.Id;
            otherUserId = otherUser.Id;
        }

        using var testScope = factory.Services.CreateScope();

        var testDb = testScope.ServiceProvider
            .GetRequiredService<ClubDbContext>();

        var repository = new EnrollmentRepository(testDb.Enrollments);

        var result = await repository.GetByIdForUserWithDetailsAsync(
            enrollmentId,
            otherUserId);

        Assert.IsNull(result);
    }
}
