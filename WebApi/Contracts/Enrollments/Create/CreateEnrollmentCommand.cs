namespace WebApi.Contracts.Enrollments.Create;

public sealed record CreateEnrollmentCommand(
    long UserId,
    long ClassId,
    int Seats,
    string IdempotencyKey);
