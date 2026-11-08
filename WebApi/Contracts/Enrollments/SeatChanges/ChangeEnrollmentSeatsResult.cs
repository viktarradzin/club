namespace WebApi.Contracts.Enrollments.SeatChanges;

public sealed class ChangeEnrollmentSeatsResult
{
    private ChangeEnrollmentSeatsResult(
        EnrollmentDetailsResponse? response,
        EnrollmentError? error)
    {
        Response = response;
        Error = error;
    }

    public EnrollmentDetailsResponse? Response { get; }

    public EnrollmentError? Error { get; }

    public static ChangeEnrollmentSeatsResult Success(
        EnrollmentDetailsResponse response)
    {
        return new ChangeEnrollmentSeatsResult(response, null);
    }

    public static ChangeEnrollmentSeatsResult Failure(
        EnrollmentError error)
    {
        return new ChangeEnrollmentSeatsResult(null, error);
    }
}
