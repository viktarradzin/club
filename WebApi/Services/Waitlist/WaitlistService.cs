using WebApi.Contracts.Enrollments.Waitlist;
using WebApi.Services.Enrollments;
using WebApi.Services.Transactions;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Waitlist;

public sealed class WaitlistService : IWaitlistService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITransactionExecutor _transactionExecutor;
    private readonly IWaitlistIdempotencyService _idempotencyService;

    public WaitlistService(
        IUnitOfWork unitOfWork,
        ITransactionExecutor transactionExecutor,
        IWaitlistIdempotencyService idempotencyService)
    {
        _unitOfWork = unitOfWork;
        _transactionExecutor = transactionExecutor;
        _idempotencyService = idempotencyService;
    }

    public Task<JoinWaitlistResult> JoinAsync(
        JoinWaitlistCommand command,
        CancellationToken ct)
    {
        return _transactionExecutor.ExecuteAsync(
            token => JoinWithinTransactionAsync(command, token),
            ct);
    }

    public Task<CancelWaitlistResult> CancelAsync(long id, long userId, CancellationToken ct)
    {
        return _transactionExecutor.ExecuteAsync(
            token => CancelWithinTransactionAsync(id, userId, token), ct);
    }

    private async Task<CancelWaitlistResult> CancelWithinTransactionAsync(
        long id,
        long userId,
        CancellationToken ct)
    {
        var entry = await _unitOfWork.Waitlist.GetByIdForUserAsync(id, userId, ct);
        if (entry is null)
        {
            return CancelWaitlistResult.Failure(EnrollmentError.WaitlistEntryNotFound);
        }

        // решение: отмена исполненной заявки сохраняет созданную бронь,
        // поскольку у брони отдельные правила отмены и возврата посещений.
        if (entry.Status != "cancelled")
        {
            entry.Status = "cancelled";
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return CancelWaitlistResult.Success(new CancelWaitlistResponse(entry.Id, "cancelled"));
    }

    private async Task<JoinWaitlistResult> JoinWithinTransactionAsync(
        JoinWaitlistCommand command,
        CancellationToken ct)
    {
        var previousResult = await _idempotencyService.GetResultAsync(command, ct);

        if (previousResult is not null)
        {
            return previousResult;
        }

        var classEntity = await _unitOfWork.Class
            .GetByIdIncludingCancelledAsync(command.ClassId, ct);

        var classError = EnrollmentRules.GetClassError(classEntity, DateTime.UtcNow);
        if (classError is not null)
        {
            return JoinWaitlistResult.Failure(classError.Value);
        }

        var alreadyWaiting = await _unitOfWork.Waitlist.IsWaitingAsync(
            command.UserId, command.ClassId, ct);
        if (alreadyWaiting)
        {
            return JoinWaitlistResult.Failure(EnrollmentError.AlreadyWaiting);
        }

        var occupiedSeats = await _unitOfWork.Enrollment.GetOccupiedSeatsAsync(
            command.ClassId, ct);
        var availableSeats = classEntity!.Capacity - occupiedSeats;
        if (availableSeats >= command.Seats)
        {
            return JoinWaitlistResult.Failure(EnrollmentError.SeatsAvailable);
        }

        var now = DateTime.UtcNow;

        var createdAt = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);

        var entry = new WaitlistEntry
        {
            UserId = command.UserId,
            ClassId = command.ClassId,
            Seats = command.Seats,
            Status = "waiting",
            CreatedAt = createdAt
        };

        await _unitOfWork.Waitlist.CreateAsync(entry, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var waitingBefore = await _unitOfWork.Waitlist.CountWaitingBeforeAsync(
            entry.ClassId, entry.CreatedAt, entry.Id, ct);
        var response = new JoinWaitlistResponse(
            entry.Id, entry.ClassId, entry.Seats, waitingBefore + 1, entry.CreatedAt);

        await _idempotencyService.StoreAsync(command, response, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return JoinWaitlistResult.Success(response);
    }
}
