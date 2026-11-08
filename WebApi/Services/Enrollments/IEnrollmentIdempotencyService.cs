using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Enrollments;

public interface IEnrollmentIdempotencyService
{
    Task<CreateEnrollmentResult?> GetResultAsync(
        CreateEnrollmentCommand command,
        CancellationToken ct);

    Task StoreAsync(
        CreateEnrollmentCommand command,
        CreateEnrollmentResponse response,
        CancellationToken ct);
}
