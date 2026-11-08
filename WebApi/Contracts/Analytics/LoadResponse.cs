namespace WebApi.Contracts.Analytics;

public class LoadResponse
{
    public IReadOnlyList<LoadItemResponse> Items { get; init; } = [];
}
