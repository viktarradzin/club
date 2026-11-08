namespace WebApi.Contracts.Enrollments.Cancellation;

public sealed record CancelEnrollmentResponse(
    long Id,
    string Status,
    int RefundedVisits);
