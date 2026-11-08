using DataAccessLayer.Entities;

namespace WebApi.Contracts.Enrollments.Cancellation;

public sealed class CancellationPreparationResult
{
    private CancellationPreparationResult(Enrollment? enrollment, EnrollmentError? error)
    {
        Enrollment = enrollment;
        Error = error;
    }

    public Enrollment? Enrollment { get; }

    public EnrollmentError? Error { get; }

    public static CancellationPreparationResult Success(Enrollment enrollment)
    {
        return new CancellationPreparationResult(enrollment, null);
    }

    public static CancellationPreparationResult Failure(EnrollmentError error)
    {
        return new CancellationPreparationResult(null, error);
    }
}
