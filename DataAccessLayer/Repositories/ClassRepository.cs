using System.Linq.Expressions;
using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class ClassRepository : GenericRepository<Class>, IClassRepository
{
    public ClassRepository(DbSet<Class> dbSet)
        : base(dbSet)
    {
    }

    public async Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(x => x.Enrollments)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<Class?> GetByIdIncludingCancelledAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<List<ScheduleClassRow>> GetSchedulePageAsync(
        SchedulePageQuery request, CancellationToken ct = default)
    {
        // решение: при усечении границ до микросекунд меняем строгость сравнения,
        // чтобы сохранить точный интервал [from, to) для входных дат .NET.
        var from = TruncateToMicroseconds(request.From);
        var to = TruncateToMicroseconds(request.To);
        var query = _dbSet.AsNoTracking();
        query = from == request.From
            ? query.Where(x => x.StartsAt >= from)
            : query.Where(x => x.StartsAt > from);
        query = to == request.To
            ? query.Where(x => x.StartsAt < to)
            : query.Where(x => x.StartsAt <= to);

        if (request.RoomId.HasValue)
        {
            query = query.Where(x => x.RoomId == request.RoomId.Value);
        }

        if (request.AfterStartsAt.HasValue && request.AfterClassId.HasValue)
        {
            query = query.Where(x =>
                x.StartsAt > request.AfterStartsAt.Value ||
                (x.StartsAt == request.AfterStartsAt.Value &&
                 x.Id > request.AfterClassId.Value));
        }

        return await query
            .OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Take(request.Take)
            .Select(CreateScheduleProjection(request.UserId))
            .ToListAsync(ct);
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    public async Task<List<RoomLoadRow>> GetRoomLoadAsync(
        DateOnly from,
        DateOnly to,
        long? roomId,
        CancellationToken ct = default)
    {
        var classRows = FilterClasses(from, to, roomId).Select(x => new
        {
            Date = DateOnly.FromDateTime(
                DateTime.SpecifyKind(x.StartsAt, DateTimeKind.Unspecified)),
            x.RoomId,
            RoomName = x.Room.Name,
            x.Capacity,
            BookedSeats = x.Enrollments
                .Where(enrollment => enrollment.Status == "active")
                .Sum(enrollment => (long)enrollment.Seats)
        });

        return await classRows
            .GroupBy(x => new { x.Date, x.RoomId, x.RoomName })
            .Select(group => new RoomLoadRow
            {
                Date = group.Key.Date,
                RoomId = group.Key.RoomId,
                RoomName = group.Key.RoomName,
                ClassesCount = group.Count(),
                TotalCapacity = group.Sum(x => (long)x.Capacity),
                BookedSeats = group.Sum(x => x.BookedSeats)
            })
            .OrderBy(x => x.Date)
            .ThenBy(x => x.RoomId)
            .ToListAsync(ct);
    }

    private IQueryable<Class> FilterClasses(DateOnly from, DateOnly to, long? roomId)
    {
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var query = _dbSet.AsNoTracking().Where(x => x.StartsAt >= fromUtc);

        if (to < DateOnly.MaxValue)
        {
            var endUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.StartsAt < endUtc);
        }

        if (roomId.HasValue)
        {
            query = query.Where(x => x.RoomId == roomId.Value);
        }

        return query;
    }

    private static Expression<Func<Class, ScheduleClassRow>>
        CreateScheduleProjection(long userId)
    {
        return x => new ScheduleClassRow
        {
            ClassId = x.Id,
            Title = x.Title,
            StartsAt = x.StartsAt,
            DurationMinutes = x.DurationMinutes,
            RoomId = x.RoomId,
            RoomName = x.Room.Name,
            Capacity = x.Capacity,
            OccupiedSeats = x.Enrollments
                .Where(e => e.Status == "active")
                .Sum(e => (long)e.Seats),
            MySeats = x.Enrollments
                .Where(e => e.Status == "active" && e.UserId == userId)
                .Sum(e => (long)e.Seats)
        };
    }
}
