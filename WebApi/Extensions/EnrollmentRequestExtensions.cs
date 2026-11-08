using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.SeatChanges;
using WebApi.Contracts.Enrollments.Waitlist;
using WebApi.Contracts.Enrollments;

namespace WebApi.Extensions;

public static class EnrollmentRequestExtensions
{
    public static JoinWaitlistCommand ToCommand(
        this JoinWaitlistRequest request,
        long userId,
        string idempotencyKey)
    {
        return new JoinWaitlistCommand(
            userId,
            request.ClassId!.Value,
            request.Seats!.Value,
            idempotencyKey);
    }

    public static CreateEnrollmentCommand ToCommand(
        this CreateEnrollmentRequest request,
        long userId,
        string idempotencyKey)
    {
        return new CreateEnrollmentCommand(
            userId,
            request.ClassId!.Value,
            request.Seats!.Value,
            idempotencyKey);
    }

    public static ChangeEnrollmentSeatsCommand ToCommand(
        this ChangeEnrollmentSeatsRequest request,
        long userId,
        long enrollmentId)
    {
        return new ChangeEnrollmentSeatsCommand(
            userId,
            enrollmentId,
            request.Seats!.Value);
    }
}
