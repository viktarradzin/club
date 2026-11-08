using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Repositories;

[TestClass]
public class PassRepositoryTests
{
    [TestMethod]
    public async Task GetByUserAsync_WhenPassExists_ReturnsUsersPass()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();

        long userId;
        long passId;
        var validUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var user = new User
            {
                Email = $"pass-{Guid.NewGuid():N}@test.local",
                PasswordHash = "test",
                Role = "user",
                CreatedAt = DateTime.UtcNow
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var pass = new Pass
            {
                UserId = user.Id,
                RemainingVisits = 8,
                ValidUntil = validUntil
            };
            db.Passes.Add(pass);
            await db.SaveChangesAsync();

            userId = user.Id;
            passId = pass.Id;
        }

        using var testScope = factory.Services.CreateScope();
        var testDb = testScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var repository = new PassRepository(testDb.Passes);

        var result = await repository.GetByUserAsync(userId);

        Assert.IsNotNull(result);
        Assert.AreEqual(passId, result.Id);
        Assert.AreEqual(userId, result.UserId);
        Assert.AreEqual(8, result.RemainingVisits);
        Assert.AreEqual(validUntil, result.ValidUntil);
    }
}
