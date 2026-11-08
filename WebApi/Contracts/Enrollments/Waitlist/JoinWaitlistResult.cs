namespace WebApi.Contracts.Enrollments.Waitlist;

public sealed class JoinWaitlistResult
{
    private JoinWaitlistResult(
        JoinWaitlistResponse? response,
        EnrollmentError? error)
    {
        Response = response;
        Error = error;
    }

    public JoinWaitlistResponse? Response { get; }

    public EnrollmentError? Error { get; }

    public static JoinWaitlistResult Success(
        JoinWaitlistResponse response)
    {
        return new JoinWaitlistResult(response, null);
    }

    public static JoinWaitlistResult Failure(
        EnrollmentError error)
    {
        return new JoinWaitlistResult(null, error);
    }
}
