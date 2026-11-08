using DataAccessLayer.Entities;

namespace WebApi.Contracts.Enrollments;

public sealed class EnrollmentPreparationResult
{
    private EnrollmentPreparationResult(Pass? pass, EnrollmentError? error)
    {
        Pass = pass;
        Error = error;
    }

    public Pass? Pass { get; }

    public EnrollmentError? Error { get; }

    public static EnrollmentPreparationResult Success(Pass pass)
    {
        return new EnrollmentPreparationResult(pass, null);
    }

    public static EnrollmentPreparationResult Failure(EnrollmentError error)
    {
        return new EnrollmentPreparationResult(null, error);
    }
}
