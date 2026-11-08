namespace WebApi.Contracts.Enrollments.Create;

public sealed class CreateEnrollmentResult
{
    private CreateEnrollmentResult(
        CreateEnrollmentResponse? response,
        EnrollmentError? error)
    {
        Response = response;
        Error = error;
    }

    public CreateEnrollmentResponse? Response { get; }

    public EnrollmentError? Error { get; }

    public static CreateEnrollmentResult Success(
        CreateEnrollmentResponse response)
    {
        return new CreateEnrollmentResult(response, null);
    }

    public static CreateEnrollmentResult Failure(
        EnrollmentError error)
    {
        return new CreateEnrollmentResult(null, error);
    }
}
