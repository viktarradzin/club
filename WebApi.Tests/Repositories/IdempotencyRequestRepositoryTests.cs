using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Repositories;

[TestClass]
public class IdempotencyRequestRepositoryTests
{
    [TestMethod]
    public async Task GetByKeyAsync_WithDifferentKeys_MatchesUserOperationHashAndFullKey()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        long userId;
        long otherUserId;
        long requestId;

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var user = NewUser();
            var otherUser = NewUser();
            db.Users.AddRange(user, otherUser);
            await db.SaveChangesAsync();

            var request = NewRequest(user.Id);
            db.IdempotencyRequests.Add(request);
            await db.SaveChangesAsync();

            userId = user.Id;
            otherUserId = otherUser.Id;
            requestId = request.Id;
        }

        using var testScope = factory.Services.CreateScope();
        var testDb = testScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new IdempotencyRequestRepository(testDb.IdempotencyRequests);

        var found = await repository.GetByKeyAsync(userId, "create-enrollment", "test-hash", "test-key");
        Assert.IsNotNull(found);
        Assert.AreEqual(requestId, found.Id);
        Assert.AreEqual(EntityState.Detached, testDb.Entry(found).State);

        Assert.IsNull(await repository.GetByKeyAsync(otherUserId, "create-enrollment", "test-hash", "test-key"));
        Assert.IsNull(await repository.GetByKeyAsync(userId, "other-operation", "test-hash", "test-key"));
        Assert.IsNull(await repository.GetByKeyAsync(userId, "create-enrollment", "other-hash", "test-key"));
        Assert.IsNull(await repository.GetByKeyAsync(userId, "create-enrollment", "test-hash", "other-key"));
    }

    [TestMethod]
    public async Task CreateAsync_BeforeSaveChanges_DoesNotPersistRequest()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        long userId;

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var user = NewUser();
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        using var writeScope = factory.Services.CreateScope();
        var writeDb = writeScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new IdempotencyRequestRepository(writeDb.IdempotencyRequests);
        var request = NewRequest(userId);

        var result = await repository.CreateAsync(request);
        Assert.AreSame(request, result);
        Assert.AreEqual(EntityState.Added, writeDb.Entry(request).State);

        using var readScope = factory.Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        Assert.IsFalse(await readDb.IdempotencyRequests.AnyAsync(x => x.UserId == userId));

        await writeDb.SaveChangesAsync();

        var saved = await readDb.IdempotencyRequests.SingleAsync(x => x.Id == request.Id);
        Assert.AreEqual(userId, saved.UserId);
        Assert.AreEqual("test-key", saved.Key);
        Assert.AreEqual("test-hash", saved.KeyHash);
        Assert.AreEqual("create-enrollment", saved.Operation);
        Assert.AreEqual(2, saved.Seats);
    }

    private static User NewUser() => new()
    {
        Email = $"idempotency-{Guid.NewGuid():N}@test.local",
        PasswordHash = "test",
        Role = "user",
        CreatedAt = DateTime.UtcNow
    };

    private static IdempotencyRequest NewRequest(long userId) => new()
    {
        UserId = userId,
        Operation = "create-enrollment",
        Key = "test-key",
        KeyHash = "test-hash",
        ClassId = 1,
        Seats = 2,
        ResponseBody = "{}",
        CreatedAt = DateTime.UtcNow
    };
}
