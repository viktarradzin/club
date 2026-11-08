using WebApi.Services.Caching;
using Common.Caching;
using DataAccessLayer.Models;
using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Schedule;

namespace WebApi.Services.Schedule;

public class ScheduleService : IScheduleService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cacheService;
    private readonly ICardCacheVersionService _cacheVersionService;

    public ScheduleService(
        IUnitOfWork unitOfWork,
        ICacheService cacheService,
        ICardCacheVersionService cacheVersionService)
    {
        _unitOfWork = unitOfWork;
        _cacheService = cacheService;
        _cacheVersionService = cacheVersionService;
    }

    public async Task<ScheduleResult> GetAsync(
        ScheduleQuery query,
        long userId,
        CancellationToken ct = default)
    {
        if (!ScheduleQueryValidator.TryValidatePeriod(query,
                out var from, out var to, out var error))
        {
            return ScheduleResult.Failure(error!);
        }

        var tokenError = ReadContinuation(
            query, from, to,out var continuation);

        if (tokenError is not null)
        {
            return ScheduleResult.Failure(tokenError);
        }

        var pageQuery = CreatePageQuery(query, userId, from, to, continuation);
        var version = await _cacheVersionService.GetGlobalAsync(ct);
        var cacheKey = ScheduleCacheKey.Create(version, pageQuery);
        var cached = await _cacheService.GetAsync<ScheduleResponse>(cacheKey, ct);

        if (cached is not null)
        {
            return ScheduleResult.Success(cached);
        }

        var rows = await _unitOfWork.Class.GetSchedulePageAsync(pageQuery, ct);

        var response = CreateResponse(rows, query, from, to);

        await _cacheService.SetAsync(cacheKey, response, ct: ct);

        return ScheduleResult.Success(response);
    }

    private static string? ReadContinuation(
        ScheduleQuery query,
        DateTime from,
        DateTime to,
        out ScheduleContinuation? continuation)
    {
        continuation = null;

        if (query.ContinuationToken is null)
        {
            return null;
        }

        if (!ScheduleContinuationTokenService.TryDecode(
                query.ContinuationToken,
                out continuation))
        {
            return "Invalid continuation token";
        }

        if (continuation!.From != from ||
            continuation.To != to ||
            continuation.RoomId != query.RoomId)
        {
            return "Continuation token does not match the query";
        }

        return null;
    }

    private static SchedulePageQuery CreatePageQuery(
        ScheduleQuery query,
        long userId,
        DateTime from,
        DateTime to,
        ScheduleContinuation? continuation)
    {
        return new SchedulePageQuery
        {
            From = from,
            To = to,
            RoomId = query.RoomId,
            UserId = userId,
            Take = query.Limit + 1,
            AfterStartsAt = continuation?.LastStartsAt,
            AfterClassId = continuation?.LastClassId
        };
    }

    private static ScheduleResponse CreateResponse(
        List<ScheduleClassRow> rows,
        ScheduleQuery query,
        DateTime from,
        DateTime to)
    {
        var hasMore = rows.Count > query.Limit;
        var pageRows = rows.Take(query.Limit).ToList();

        var nextToken = hasMore
            ? CreateNextToken(pageRows[^1], query.RoomId, from, to)
            : null;

        return new ScheduleResponse
        {
            Items = pageRows.Select(ToResponse).ToList(),
            ContinuationToken = nextToken
        };
    }

    private static string CreateNextToken(
        ScheduleClassRow lastRow,
        long? roomId,
        DateTime from,
        DateTime to)
    {
        var continuation = new ScheduleContinuation
        {
            Version = 1,
            From = from,
            To = to,
            RoomId = roomId,
            LastStartsAt = lastRow.StartsAt,
            LastClassId = lastRow.ClassId
        };

        return ScheduleContinuationTokenService.Encode(continuation);
    }

    private static ScheduleItemResponse ToResponse(ScheduleClassRow row)
    {
        return new ScheduleItemResponse
        {
            ClassId = row.ClassId,
            Title = row.Title,
            StartsAt = row.StartsAt,
            DurationMinutes = row.DurationMinutes,
            Room = new ScheduleRoomResponse
            {
                Id = row.RoomId,
                Name = row.RoomName
            },
            Capacity = row.Capacity,
            FreeSeats = (int)Math.Max(0L, row.Capacity - row.OccupiedSeats),
            MySeats = checked((int)row.MySeats)
        };
    }
}
