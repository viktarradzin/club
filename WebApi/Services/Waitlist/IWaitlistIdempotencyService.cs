using WebApi.Contracts.Enrollments.Waitlist;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Waitlist;

public interface IWaitlistIdempotencyService
{
    Task<JoinWaitlistResult?> GetResultAsync(
        JoinWaitlistCommand command,
        CancellationToken ct);

    Task StoreAsync(
        JoinWaitlistCommand command,
        JoinWaitlistResponse response,
        CancellationToken ct);
}
