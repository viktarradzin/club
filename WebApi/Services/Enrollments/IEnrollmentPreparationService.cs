using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.Cancellation;
using WebApi.Contracts.Enrollments.SeatChanges;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Enrollments;

public interface IEnrollmentPreparationService
{
    Task<EnrollmentPreparationResult> PrepareAsync(
        CreateEnrollmentCommand command,
        DateTime now,
        CancellationToken ct);

    Task<SeatChangePreparationResult> PrepareSeatChangeAsync(
        ChangeEnrollmentSeatsCommand command,
        DateTime now,
        CancellationToken ct);

    Task<CancellationPreparationResult> PrepareCancellationAsync(
        long enrollmentId,
        long userId,
        DateTime now,
        CancellationToken ct);
}
