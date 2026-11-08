using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebApi.Services.Transactions;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Services;

[TestClass]
public class TransactionExecutorTests
{
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private IServiceScope _scope = null!;
    private ClubDbContext _db = null!;
    private ITransactionExecutor _executor = null!;
    private long _userId;

    [TestInitialize]
    public async Task SetUp()
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
        _scope = _factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        _executor = new TransactionExecutor(new UnitOfWork(_db));
        var user = new User
        {
            Email = $"transaction-{Guid.NewGuid():N}@test.local", PasswordHash = "unused",
            Role = "user", CreatedAt = DateTime.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _db.Passes.Add(new Pass
        {
            UserId = user.Id, RemainingVisits = 10, ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
        });
        await _db.SaveChangesAsync();
        _userId = user.Id;
        _db.ChangeTracker.Clear();
    }

    [TestCleanup]
    public void CleanUp()
    {
        _scope?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [DataTestMethod]
    [DataRow(PostgresErrorCodes.SerializationFailure)]
    [DataRow(PostgresErrorCodes.DeadlockDetected)]
    public async Task ExecuteAsync_AfterFiveTransientConflicts_CommitsOnlyFinalDebit(string sqlState)
    {
        var attempts = 0;
        var result = await _executor.ExecuteAsync(async ct =>
        {
            attempts++;
            await DebitAsync(ct);
            if (attempts <= 5) throw new InvalidOperationException("Wrapped conflict", Conflict(sqlState));
            return "committed";
        }, CancellationToken.None);

        Assert.AreEqual("committed", result);
        Assert.AreEqual(6, attempts);
        Assert.AreEqual(9, await RemainingVisitsAsync());
        Assert.IsNull(_db.Database.CurrentTransaction);
    }

    [TestMethod]
    public async Task ExecuteAsync_PermanentDatabaseError_IsNotRetriedAndRollsBack()
    {
        var attempts = 0;
        var error = Conflict(PostgresErrorCodes.UniqueViolation);
        var thrown = await Assert.ThrowsExceptionAsync<PostgresException>(() => _executor.ExecuteAsync<int>(async ct =>
        {
            attempts++;
            await DebitAsync(ct);
            throw error;
        }, CancellationToken.None));

        Assert.AreSame(error, thrown);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(10, await RemainingVisitsAsync());
    }

    [TestMethod]
    public async Task ExecuteAsync_CancelledDuringRetry_StopsAndRollsBack()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => _executor.ExecuteAsync<int>(async ct =>
        {
            attempts++;
            await DebitAsync(ct);
            cancellation.Cancel();
            throw Conflict(PostgresErrorCodes.SerializationFailure);
        }, cancellation.Token));

        Assert.AreEqual(1, attempts);
        Assert.AreEqual(10, await RemainingVisitsAsync());
    }

    private async Task DebitAsync(CancellationToken ct)
    {
        var pass = await _db.Passes.SingleAsync(x => x.UserId == _userId, ct);
        pass.RemainingVisits--;
        await _db.SaveChangesAsync(ct);
    }

    private Task<int> RemainingVisitsAsync() => _db.Passes.AsNoTracking()
        .Where(x => x.UserId == _userId).Select(x => x.RemainingVisits).SingleAsync();

    private static PostgresException Conflict(string sqlState) => new("Test conflict", "ERROR", "ERROR", sqlState);
}
