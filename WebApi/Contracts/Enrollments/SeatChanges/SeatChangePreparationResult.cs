using DataAccessLayer.Entities;

namespace WebApi.Contracts.Enrollments.SeatChanges;

public sealed class SeatChangePreparationResult
{
    private SeatChangePreparationResult(
        Enrollment? enrollment,
        Pass? pass,
        EnrollmentError? error)
    {
        Enrollment = enrollment;
        Pass = pass;
        Error = error;
    }

    public Enrollment? Enrollment { get; }

    public Pass? Pass { get; }

    public EnrollmentError? Error { get; }

    public static SeatChangePreparationResult Success(
        Enrollment enrollment,
        Pass pass)
    {
        return new SeatChangePreparationResult(enrollment, pass, null);
    }

    public static SeatChangePreparationResult Failure(
        EnrollmentError error)
    {
        return new SeatChangePreparationResult(null, null, error);
    }
}
