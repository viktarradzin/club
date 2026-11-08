using WebApi.Services.Caching;
using Common.Caching;
using DataAccessLayer.Models;
using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Analytics;

namespace WebApi.Services.Analytics;

public class AnalyticsService : IAnalyticsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cacheService;
    private readonly ICardCacheVersionService _cacheVersionService;

    public AnalyticsService(
        IUnitOfWork unitOfWork,
        ICacheService cacheService,
        ICardCacheVersionService cacheVersionService)
    {
        _unitOfWork = unitOfWork;
        _cacheService = cacheService;
        _cacheVersionService = cacheVersionService;
    }

    public async Task<LoadResult> GetLoadAsync(
        LoadQuery query,
        CancellationToken ct = default)
    {
        if (!LoadQueryValidator.TryValidatePeriod(
                query, out var from, out var to, out var error))
        {
            return LoadResult.Failure(error!);
        }

        var version = await _cacheVersionService.GetGlobalAsync(ct);
        var cacheKey = CreateCacheKey(version, from, to, query.RoomId);
        var cached = await _cacheService.GetAsync<LoadResponse>(cacheKey, ct);

        if (cached is not null)
        {
            return LoadResult.Success(cached);
        }

        var rows = await _unitOfWork.Class.GetRoomLoadAsync(
            from, to, query.RoomId, ct);

        var response = new LoadResponse
        {
            Items = rows.Select(ToResponse).ToList()
        };

        await _cacheService.SetAsync(cacheKey, response, ct: ct);

        return LoadResult.Success(response);
    }

    private static string CreateCacheKey(long version, DateOnly from, DateOnly to, long? roomId)
    {
        var room = roomId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all";
        return FormattableString.Invariant(
            $"analytics:load:v3:{version}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{room}");
    }

    private static LoadItemResponse ToResponse(RoomLoadRow row)
    {
        return new LoadItemResponse
        {
            Date = row.Date,
            Room = new LoadRoomResponse
            {
                Id = row.RoomId,
                Name = row.RoomName
            },
            ClassesCount = row.ClassesCount,
            TotalCapacity = checked((int)row.TotalCapacity),
            BookedSeats = checked((int)row.BookedSeats)
        };
    }
}
