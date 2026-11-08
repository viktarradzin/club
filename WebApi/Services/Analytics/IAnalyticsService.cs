using WebApi.Contracts.Analytics;

namespace WebApi.Services.Analytics;

public interface IAnalyticsService
{
    Task<LoadResult> GetLoadAsync(
        LoadQuery query,
        CancellationToken ct = default);
}
