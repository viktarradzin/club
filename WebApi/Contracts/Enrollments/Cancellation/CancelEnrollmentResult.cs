namespace WebApi.Contracts.Enrollments.Cancellation;

public sealed class CancelEnrollmentResult
{
    private CancelEnrollmentResult(
        CancelEnrollmentResponse? response,
        EnrollmentError? error)
    {
        Response = response;
        Error = error;
    }

    public CancelEnrollmentResponse? Response { get; }

    public EnrollmentError? Error { get; }

    public static CancelEnrollmentResult Success(
        CancelEnrollmentResponse response)
    {
        return new CancelEnrollmentResult(response, null);
    }

    public static CancelEnrollmentResult Failure(
        EnrollmentError error)
    {
        return new CancelEnrollmentResult(null, error);
    }
}
