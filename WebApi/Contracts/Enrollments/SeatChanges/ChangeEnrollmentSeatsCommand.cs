namespace WebApi.Contracts.Enrollments.SeatChanges;

public sealed record ChangeEnrollmentSeatsCommand(
    long UserId,
    long EnrollmentId,
    int Seats);
