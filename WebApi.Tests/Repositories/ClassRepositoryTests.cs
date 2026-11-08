using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Repositories;

[TestClass]
public class ClassRepositoryTests
{
    [TestMethod]
    public async Task GetRoomLoadAsync_WithClasses_GroupsAndCountsActiveSeats()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        var date = new DateOnly(2035, 1, 15);
        long roomId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var user = db.Users.First();
            var room = new Room { Name = "Analytics test room" };
            var firstClass = new Class
            {
                Title = "First class", Room = room,
                StartsAt = date.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc),
                DurationMinutes = 60, Capacity = 10
            };
            var secondClass = new Class
            {
                Title = "Second class", Room = room,
                StartsAt = date.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc),
                DurationMinutes = 60, Capacity = 20
            };
            db.Classes.AddRange(firstClass, secondClass);
            await db.SaveChangesAsync();
            db.Enrollments.AddRange(
                new Enrollment
                {
                    UserId = user.Id, ClassId = firstClass.Id, Seats = 3, Status = "active", CreatedAt = DateTime.UtcNow
                },
                new Enrollment
                {
                    UserId = user.Id, ClassId = firstClass.Id, Seats = 2, Status = "active", CreatedAt = DateTime.UtcNow
                },
                new Enrollment
                {
                    UserId = user.Id, ClassId = firstClass.Id, Seats = 4, Status = "cancelled",
                    CreatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        using var testScope = factory.Services.CreateScope();
        var testDb = testScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new ClassRepository(testDb.Classes);
        var result = await repository.GetRoomLoadAsync(date, date, roomId);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(date, result[0].Date);
        Assert.AreEqual(roomId, result[0].RoomId);
        Assert.AreEqual(2, result[0].ClassesCount);
        Assert.AreEqual(30L, result[0].TotalCapacity);
        Assert.AreEqual(5L, result[0].BookedSeats);
    }

    [TestMethod]
    public async Task GetByIdIncludingCancelledAsync_WhenClassIsCancelled_ReturnsClass()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();

        long classId;
        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var classEntity = new Class
            {
                Title = "Cancelled class",
                Room = new Room { Name = "Test room" },
                StartsAt = DateTime.UtcNow.AddDays(1),
                DurationMinutes = 60,
                Capacity = 10,
                IsCancelled = true
            };

            db.Classes.Add(classEntity);
            await db.SaveChangesAsync();
            classId = classEntity.Id;
        }

        using var testScope = factory.Services.CreateScope();
        var testDb = testScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new ClassRepository(testDb.Classes);

        var result = await repository.GetByIdIncludingCancelledAsync(classId);

        Assert.IsNotNull(result);
        Assert.AreEqual(classId, result.Id);
        Assert.AreEqual("Cancelled class", result.Title);
        Assert.IsTrue(result.IsCancelled);
    }
}
