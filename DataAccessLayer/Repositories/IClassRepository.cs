using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using DataAccessLayer.Models;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);

    Task<Class?> GetByIdIncludingCancelledAsync(long id, CancellationToken ct = default);

    Task<List<ScheduleClassRow>> GetSchedulePageAsync(SchedulePageQuery request, CancellationToken ct = default);

    Task<List<RoomLoadRow>> GetRoomLoadAsync(DateOnly from, DateOnly to, long? roomId, CancellationToken ct = default);
}
