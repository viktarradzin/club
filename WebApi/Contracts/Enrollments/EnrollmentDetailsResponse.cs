namespace WebApi.Contracts.Enrollments;

public sealed record EnrollmentDetailsResponse(long Id, string Status, int Seats,
    DateTime CreatedAt, EnrollmentClassResponse Class, EnrollmentPassResponse Pass);

public sealed record EnrollmentClassResponse(long Id, string Title, DateTime StartsAt,
    int DurationMinutes, bool IsCancelled, EnrollmentRoomResponse Room);

public sealed record EnrollmentRoomResponse(long Id, string Name);

public sealed record EnrollmentPassResponse(int RemainingVisits, DateOnly ValidUntil);
