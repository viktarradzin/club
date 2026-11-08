namespace WebApi.Services.Transactions;

public interface ITransactionExecutor
{
    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct);
}
