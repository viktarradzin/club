using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.Cancellation;
using WebApi.Contracts.Enrollments.SeatChanges;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Enrollments;

public sealed class EnrollmentPreparationService : IEnrollmentPreparationService
{
    private readonly IUnitOfWork _unitOfWork;

    public EnrollmentPreparationService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<EnrollmentPreparationResult> PrepareAsync(
        CreateEnrollmentCommand command,
        DateTime now,
        CancellationToken ct)
    {
        var classEntity = await _unitOfWork.Class
            .GetByIdIncludingCancelledAsync(command.ClassId, ct);

        var error = EnrollmentRules.GetClassError(classEntity, now);
        if (error is not null)
        {
            return EnrollmentPreparationResult.Failure(error.Value);
        }

        return await PreparePassAsync(command, classEntity!, now, ct);
    }

    public async Task<SeatChangePreparationResult> PrepareSeatChangeAsync(
        ChangeEnrollmentSeatsCommand command,
        DateTime now,
        CancellationToken ct)
    {
        var enrollment = await _unitOfWork.Enrollment
            .GetByIdForUserWithDetailsAsync(
                command.EnrollmentId,
                command.UserId,
                ct);

        var error = EnrollmentRules.GetEnrollmentError(enrollment)
                    ?? EnrollmentRules.GetClassError(enrollment!.Class, now);

        if (error is not null)
        {
            return SeatChangePreparationResult.Failure(error.Value);
        }

        return await PrepareSeatChangePassAsync(
            command,
            enrollment!,
            now,
            ct);
    }

    public async Task<CancellationPreparationResult> PrepareCancellationAsync(
        long enrollmentId,
        long userId,
        DateTime now,
        CancellationToken ct)
    {
        var enrollment = await _unitOfWork.Enrollment
            .GetByIdForUserWithDetailsAsync(enrollmentId, userId, ct);

        if (enrollment is null)
        {
            return CancellationPreparationResult.Failure(EnrollmentError.EnrollmentNotFound);
        }

        if (enrollment.Status == "cancelled")
        {
            return CancellationPreparationResult.Success(enrollment);
        }

        if (!enrollment.Class.IsCancelled && enrollment.Class.StartsAt <= now)
        {
            return CancellationPreparationResult.Failure(EnrollmentError.ClassStarted);
        }

        return CancellationPreparationResult.Success(enrollment);
    }

    private async Task<EnrollmentPreparationResult> PreparePassAsync(
        CreateEnrollmentCommand command,
        Class classEntity,
        DateTime now,
        CancellationToken ct)
    {
        var pass = await _unitOfWork.Pass.GetByUserAsync(command.UserId, ct)
                   ?? throw new InvalidOperationException("User pass was not found.");

        var error = EnrollmentRules.GetPassError(pass, command.Seats, DateOnly.FromDateTime(now));
        if (error is not null)
        {
            return EnrollmentPreparationResult.Failure(error.Value);
        }

        return await PrepareCapacityAsync(command, classEntity.Capacity, pass, ct);
    }

    private async Task<EnrollmentPreparationResult> PrepareCapacityAsync(
        CreateEnrollmentCommand command,
        int capacity,
        Pass pass,
        CancellationToken ct)
    {
        var occupiedSeats = await _unitOfWork.Enrollment
            .GetOccupiedSeatsAsync(command.ClassId, ct);

        var error = EnrollmentRules.GetCapacityError(capacity, occupiedSeats, command.Seats);

        return error is not null
            ? EnrollmentPreparationResult.Failure(error.Value)
            : EnrollmentPreparationResult.Success(pass);
    }

    private async Task<EnrollmentError?> GetCapacityErrorForSeatChangeAsync(
        Enrollment enrollment,
        int delta,
        CancellationToken ct)
    {
        if (delta <= 0)
        {
            return null;
        }

        var occupiedSeats = await _unitOfWork.Enrollment
            .GetOccupiedSeatsAsync(enrollment.ClassId, ct);

        return EnrollmentRules.GetCapacityError(
            enrollment.Class.Capacity,
            occupiedSeats,
            delta);
    }

    private async Task<SeatChangePreparationResult> PrepareSeatChangePassAsync(
        ChangeEnrollmentSeatsCommand command,
        Enrollment enrollment,
        DateTime now,
        CancellationToken ct)
    {
        var pass = await _unitOfWork.Pass.GetByUserAsync(command.UserId, ct)
                   ?? throw new InvalidOperationException("User pass was not found.");

        var delta = command.Seats - enrollment.Seats;

        var error = EnrollmentRules.GetPassErrorForSeatChange(
                        pass,
                        delta,
                        DateOnly.FromDateTime(now))
                    ?? await GetCapacityErrorForSeatChangeAsync(
                        enrollment,
                        delta,
                        ct);

        return error is not null
            ? SeatChangePreparationResult.Failure(error.Value)
            : SeatChangePreparationResult.Success(enrollment, pass);
    }
}
