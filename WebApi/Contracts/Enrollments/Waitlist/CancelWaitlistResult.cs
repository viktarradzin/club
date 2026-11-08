namespace WebApi.Contracts.Enrollments.Waitlist;

public sealed class CancelWaitlistResult
{
    private CancelWaitlistResult(
        CancelWaitlistResponse? response,
        EnrollmentError? error)
    {
        Response = response;
        Error = error;
    }

    public CancelWaitlistResponse? Response { get; }

    public EnrollmentError? Error { get; }

    public static CancelWaitlistResult Success(CancelWaitlistResponse response)
    {
        return new CancelWaitlistResult(response, null);
    }

    public static CancelWaitlistResult Failure(EnrollmentError error)
    {
        return new CancelWaitlistResult(null, error);
    }
}
