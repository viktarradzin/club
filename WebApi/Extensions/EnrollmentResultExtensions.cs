using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.Cancellation;
using WebApi.Contracts.Enrollments.SeatChanges;
using WebApi.Contracts.Enrollments.Waitlist;
using Microsoft.AspNetCore.Mvc;
using WebApi.Contracts.Enrollments;

namespace WebApi.Extensions;

public static class EnrollmentResultExtensions
{
    public static IActionResult ToActionResult(
        this CancelWaitlistResult result,
        ControllerBase controller)
    {
        if (result.Response is not null)
        {
            return controller.Ok(result.Response);
        }

        return ToErrorActionResult(result.Error, controller);
    }

    public static IActionResult ToActionResult(
        this JoinWaitlistResult result,
        ControllerBase controller)
    {
        if (result.Response is not null)
        {
            return controller.StatusCode(StatusCodes.Status201Created, result.Response);
        }

        return ToErrorActionResult(result.Error, controller);
    }

    public static IActionResult ToActionResult(
        this CreateEnrollmentResult result,
        ControllerBase controller)
    {
        if (result.Response is not null)
        {
            return controller.StatusCode(
                StatusCodes.Status201Created,
                result.Response);
        }

        return ToErrorActionResult(result.Error, controller);
    }

    public static IActionResult ToActionResult(
        this ChangeEnrollmentSeatsResult result,
        ControllerBase controller)
    {
        if (result.Response is not null)
        {
            return controller.Ok(result.Response);
        }

        return ToErrorActionResult(result.Error, controller);
    }

    public static IActionResult ToActionResult(
        this EnrollmentDetailsResponse? response,
        ControllerBase controller)
    {
        if (response is null)
        {
            return controller.NotFound();
        }

        return controller.Ok(response);
    }

    private static IActionResult ToErrorActionResult(
        EnrollmentError? error,
        ControllerBase controller)
    {
        var (status, title, reason) = error switch
        {
            EnrollmentError.ClassNotFound =>
                (404, "Class not found", null),

            EnrollmentError.CapacityExceeded =>
                (409, "Not enough available seats", "capacity-exceeded"),

            EnrollmentError.PassExhausted =>
                (409, "Not enough remaining visits", "pass-exhausted"),

            EnrollmentError.PassExpired =>
                (409, "Pass has expired", "pass-expired"),

            EnrollmentError.ClassStarted =>
                (409, "Class has already started", "class-started"),

            EnrollmentError.ClassCancelled =>
                (409, "Class has been cancelled", "class-cancelled"),

            EnrollmentError.IdempotencyKeyReused =>
                (409, "Idempotency key was already used with different parameters", "idempotency-key-reused"),

            EnrollmentError.SeatsAvailable =>
                (409, "Enough seats are available", "seats-available"),

            EnrollmentError.AlreadyWaiting =>
                (409, "User is already waiting for this class", "already-waiting"),

            EnrollmentError.EnrollmentNotFound =>
                (404, "Enrollment not found", null),

            EnrollmentError.WaitlistEntryNotFound =>
                (404, "Waitlist entry not found", null),

            EnrollmentError.EnrollmentCancelled =>
                (409, "Enrollment has been cancelled", "enrollment-cancelled"),

            _ => throw new InvalidOperationException(
                "Unknown enrollment result.")
        };

        return controller.Problem(
            statusCode: status,
            title: title,
            extensions: reason is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["reason"] = reason
                });
    }

    public static IActionResult ToActionResult(
        this CancelEnrollmentResult result,
        ControllerBase controller)
    {
        if (result.Response is not null)
        {
            return controller.Ok(result.Response);
        }

        return ToErrorActionResult(result.Error, controller);
    }
}
