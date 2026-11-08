namespace WebApi.Contracts.Enrollments;

public enum EnrollmentError
{
    ClassNotFound,
    CapacityExceeded,
    PassExhausted,
    PassExpired,
    ClassStarted,
    ClassCancelled,
    IdempotencyKeyReused,
    EnrollmentNotFound,
    EnrollmentCancelled,
    SeatsAvailable,
    AlreadyWaiting,
    WaitlistEntryNotFound
}
