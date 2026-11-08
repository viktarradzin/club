namespace WebApi.Contracts.Enrollments.Waitlist;

public sealed record JoinWaitlistResponse(
    long Id,
    long ClassId,
    int Seats,
    int Position,
    DateTime CreatedAt);
