namespace WebApi.Contracts.Enrollments.Waitlist;

public sealed record JoinWaitlistCommand(
    long UserId,
    long ClassId,
    int Seats,
    string IdempotencyKey);
