using WebApi.Contracts.Schedule;

namespace WebApi.Services.Schedule;

public interface IScheduleService
{
    Task<ScheduleResult> GetAsync(
        ScheduleQuery query,
        long userId,
        CancellationToken ct = default);
}
