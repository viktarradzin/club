using DataAccessLayer.Entities;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Enrollments;

internal static class EnrollmentRules
{
    public static int GetRefundedVisits(Enrollment enrollment, DateTime now)
    {
        var classEntity = enrollment.Class;

        if (classEntity.IsCancelled)
        {
            return enrollment.Seats;
        }

        var timeUntilStart = classEntity.StartsAt - now;

        return timeUntilStart >= TimeSpan.FromHours(2)
            ? enrollment.Seats
            : 0;
    }

    public static EnrollmentError? GetClassError(
        Class? classEntity,
        DateTime now)
    {
        return classEntity switch
        {
            null => EnrollmentError.ClassNotFound,
            { IsCancelled: true } => EnrollmentError.ClassCancelled,
            { StartsAt: var startsAt } when startsAt <= now => EnrollmentError.ClassStarted,
            _ => null
        };
    }

    public static EnrollmentError? GetPassError(
        Pass pass,
        int seats,
        DateOnly today)
    {
        return pass switch
        {
            { ValidUntil: var validUntil } when validUntil < today =>
                EnrollmentError.PassExpired,
            { RemainingVisits: var remainingVisits } when remainingVisits < seats =>
                EnrollmentError.PassExhausted,
            _ => null
        };
    }

    public static EnrollmentError? GetCapacityError(
        int capacity,
        long occupiedSeats,
        int requestedSeats)
    {
        var availableSeats = capacity - occupiedSeats;

        return availableSeats < requestedSeats
            ? EnrollmentError.CapacityExceeded
            : null;
    }

    public static EnrollmentError? GetEnrollmentError(
        Enrollment? enrollment)
    {
        return enrollment switch
        {
            null => EnrollmentError.EnrollmentNotFound,
            { Status: "cancelled" } => EnrollmentError.EnrollmentCancelled,
            _ => null
        };
    }

    public static EnrollmentError? GetPassErrorForSeatChange(
        Pass pass,
        int delta,
        DateOnly today)
    {
        return delta > 0
            ? GetPassError(pass, delta, today)
            : null;
    }
}
