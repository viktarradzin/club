using WebApi.Contracts.Enrollments.Waitlist;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Waitlist;

public interface IWaitlistService
{
    Task<JoinWaitlistResult> JoinAsync(
        JoinWaitlistCommand command,
        CancellationToken ct);

    Task<CancelWaitlistResult> CancelAsync(long id, long userId, CancellationToken ct);
}
