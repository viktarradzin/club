using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IEnrollmentRepository : IGenericRepository<Enrollment>
{
    Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default);

    Task<long> GetOccupiedSeatsAsync(long classId, CancellationToken ct = default);

    Task<Enrollment?> GetByIdForUserWithDetailsAsync(long id, long userId, CancellationToken ct = default);
}
