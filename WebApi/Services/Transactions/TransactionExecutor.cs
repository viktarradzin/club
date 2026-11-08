using System.Data;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebApi.Services.Transactions;

public sealed class TransactionExecutor : ITransactionExecutor
{
    private const int MaxAttempts = 12;
    private readonly IUnitOfWork _unitOfWork;

    public TransactionExecutor(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // решение: используем Serializable с повтором всей операции при конфликте,
                // чтобы конкурентные запросы согласованно проверяли места и посещения.
                await using var transaction = await _unitOfWork.DbContext.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var result = await operation(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsTransactionConflict(exception))
            {
                _unitOfWork.DbContext.ChangeTracker.Clear();
                var maxDelayMilliseconds = Math.Min(1_000, 20 * (1 << (attempt - 1)));
                var delay = TimeSpan.FromMilliseconds(
                    Random.Shared.Next(maxDelayMilliseconds / 2, maxDelayMilliseconds + 1));
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTransactionConflict(Exception exception)
    {
        if (exception.GetBaseException() is not PostgresException error)
        {
            return false;
        }

        return error.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected ||
               (error.SqlState == PostgresErrorCodes.UniqueViolation &&
                error.ConstraintName == "IX_waitlist_entries_user_id_class_id");
    }
}
