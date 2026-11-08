namespace WebApi.Contracts.Analytics;

public class LoadResult
{
    private LoadResult(LoadResponse? response, string? error)
    {
        Response = response;
        Error = error;
    }

    public LoadResponse? Response { get; }

    public string? Error { get; }

    public static LoadResult Success(LoadResponse response)
    {
        return new LoadResult(response, null);
    }

    public static LoadResult Failure(string error)
    {
        return new LoadResult(null, error);
    }
}
