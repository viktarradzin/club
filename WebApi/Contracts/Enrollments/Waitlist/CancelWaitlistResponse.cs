namespace WebApi.Contracts.Enrollments.Waitlist;

public sealed record CancelWaitlistResponse(
    long Id,
    string Status);
