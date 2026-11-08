using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.Cancellation;
using WebApi.Contracts.Enrollments.SeatChanges;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Enrollments;

public interface IEnrollmentService
{
    Task<CreateEnrollmentResult> CreateAsync(CreateEnrollmentCommand command,
        CancellationToken ct);

    Task<ChangeEnrollmentSeatsResult> ChangeSeatsAsync(ChangeEnrollmentSeatsCommand command,
        CancellationToken ct);

    Task<CancelEnrollmentResult> CancelAsync(long enrollmentId, long userId, CancellationToken ct);

    Task<EnrollmentDetailsResponse?> GetDetailsAsync(long enrollmentId, long userId, CancellationToken ct);
}
