using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.Cancellation;
using WebApi.Contracts.Enrollments.SeatChanges;
using WebApi.Services.Waitlist;
using WebApi.Services.Caching;
using WebApi.Services.Transactions;
using Common.Caching;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Enrollments;
using WebApi.Extensions;

namespace WebApi.Services.Enrollments;

public sealed class EnrollmentService : IEnrollmentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEnrollmentPreparationService _preparationService;
    private readonly IEnrollmentIdempotencyService _idempotencyService;
    private readonly ITransactionExecutor _transactionExecutor;
    private readonly ICardCacheVersionService _cardCacheVersionService;
    private readonly ICacheService _cacheService;
    private readonly IWaitlistPromotionService _waitlistPromotionService;

    public EnrollmentService(
        IUnitOfWork unitOfWork,
        IEnrollmentPreparationService preparationService,
        IEnrollmentIdempotencyService idempotencyService,
        ITransactionExecutor transactionExecutor,
        ICardCacheVersionService cardCacheVersionService,
        ICacheService cacheService,
        IWaitlistPromotionService waitlistPromotionService)
    {
        _unitOfWork = unitOfWork;
        _preparationService = preparationService;
        _idempotencyService = idempotencyService;
        _transactionExecutor = transactionExecutor;
        _cardCacheVersionService = cardCacheVersionService;
        _cacheService = cacheService;
        _waitlistPromotionService = waitlistPromotionService;
    }

    public Task<CreateEnrollmentResult> CreateAsync(
        CreateEnrollmentCommand command,
        CancellationToken ct)
    {
        return _transactionExecutor.ExecuteAsync(token => CreateWithinTransactionAsync(command, token), ct);
    }

    public Task<ChangeEnrollmentSeatsResult> ChangeSeatsAsync(
        ChangeEnrollmentSeatsCommand command,
        CancellationToken ct)
    {
        return _transactionExecutor.ExecuteAsync(token => ChangeSeatsWithinTransactionAsync(command, token), ct);
    }

    public Task<CancelEnrollmentResult> CancelAsync(
        long enrollmentId,
        long userId,
        CancellationToken ct)
    {
        return _transactionExecutor.ExecuteAsync(
            token => CancelWithinTransactionAsync(enrollmentId, userId, token), ct);
    }

    public async Task<EnrollmentDetailsResponse?> GetDetailsAsync(
        long enrollmentId,
        long userId,
        CancellationToken ct)
    {
        // решение: включаем общую для пользователя версию в ключ карточки,
        // чтобы изменение абонемента делало кэш всех его карточек неактуальным.
        var version = await _cardCacheVersionService.GetAsync(userId, ct);
        var cacheKey = $"enrollment-card:v2:{version}:{userId}:{enrollmentId}";

        var cached = await _cacheService.GetAsync<EnrollmentDetailsResponse>(cacheKey, ct);
        if (cached is not null)
        {
            return cached;
        }

        var enrollment = await _unitOfWork.Enrollment
            .GetByIdForUserWithDetailsAsync(enrollmentId, userId, ct);

        if (enrollment is null)
        {
            return null;
        }

        var pass = await _unitOfWork.Pass.GetByUserAsync(userId, ct)
                   ?? throw new InvalidOperationException("User pass was not found.");

        var response = enrollment.ToDetailsResponse(pass);
        await _cacheService.SetAsync(cacheKey, response, ct: ct);

        return response;
    }

    private async Task<CancelEnrollmentResult> CancelWithinTransactionAsync(
        long enrollmentId,
        long userId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var preparation = await _preparationService.PrepareCancellationAsync(
            enrollmentId, userId, now, ct);

        if (preparation.Error is not null)
        {
            return CancelEnrollmentResult.Failure(preparation.Error.Value);
        }

        var enrollment = preparation.Enrollment!;

        if (enrollment.Status == "cancelled")
        {
            var refundedVisits = enrollment.RefundedVisits ?? 0;

            var response = new CancelEnrollmentResponse(
                enrollment.Id,
                enrollment.Status,
                refundedVisits);

            return CancelEnrollmentResult.Success(response);
        }

        var refund = EnrollmentRules.GetRefundedVisits(enrollment, now);

        if (refund > 0)
        {
            var pass = await _unitOfWork.Pass.GetByUserAsync(userId, ct)
                ?? throw new InvalidOperationException("User pass was not found.");

            pass.RemainingVisits += refund;
        }

        enrollment.Status = "cancelled";
        enrollment.CancelledAt = now;
        enrollment.RefundedVisits = refund;

        await _unitOfWork.SaveChangesAsync(ct);
        await _waitlistPromotionService.PromoteAsync(enrollment.ClassId, now, ct);
        await _cardCacheVersionService.IncrementAsync(userId, ct);

        var cancellationResponse = new CancelEnrollmentResponse(
            enrollment.Id,
            enrollment.Status,
            refund);

        return CancelEnrollmentResult.Success(cancellationResponse);
    }

    private async Task<CreateEnrollmentResult> CreateWithinTransactionAsync(
        CreateEnrollmentCommand command,
        CancellationToken ct)
    {
        var previousResult = await _idempotencyService.GetResultAsync(command, ct);

        if (previousResult is not null)
        {
            return previousResult;
        }

        var now = DateTime.UtcNow;
        var preparation = await _preparationService.PrepareAsync(command, now, ct);

        if (preparation.Error is not null)
        {
            return CreateEnrollmentResult.Failure(preparation.Error.Value);
        }

        var result = await CreateAndStoreResultAsync(command, preparation.Pass!, now, ct);

        return result;
    }

    private async Task<CreateEnrollmentResult> CreateAndStoreResultAsync(
        CreateEnrollmentCommand command,
        Pass pass,
        DateTime now,
        CancellationToken ct)
    {
        var enrollment = await SaveEnrollmentAsync(command, pass, now, ct);
        var response = enrollment.ToResponse();

        await _idempotencyService.StoreAsync(command, response, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        await _cardCacheVersionService.IncrementAsync(command.UserId, ct);

        return CreateEnrollmentResult.Success(response);
    }

    private async Task<Enrollment> SaveEnrollmentAsync(
        CreateEnrollmentCommand command,
        Pass pass,
        DateTime now,
        CancellationToken ct)
    {
        // решение: усекаем createdAt до микросекунд, чтобы время в ответе
        // при создании совпадало со значением после чтения из PostgreSQL.
        var createdAt = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);

        var enrollment = EnrollmentFactory.Create(command, createdAt);

        await _unitOfWork.Enrollment.CreateAsync(enrollment, ct);
        pass.RemainingVisits -= command.Seats;

        await _unitOfWork.SaveChangesAsync(ct);

        return enrollment;
    }

    private async Task<ChangeEnrollmentSeatsResult> ChangeSeatsWithinTransactionAsync(
        ChangeEnrollmentSeatsCommand command,
        CancellationToken ct)
    {
        var preparation = await _preparationService.PrepareSeatChangeAsync(
            command,
            DateTime.UtcNow,
            ct);

        if (preparation.Error is not null)
        {
            return ChangeEnrollmentSeatsResult.Failure(preparation.Error.Value);
        }

        var enrollment = preparation.Enrollment!;
        var pass = preparation.Pass!;

        await SaveSeatChangeAsync(enrollment, pass, command.Seats, ct);

        return ChangeEnrollmentSeatsResult.Success(
            enrollment.ToDetailsResponse(pass));
    }

    private async Task SaveSeatChangeAsync(Enrollment enrollment, Pass pass,
        int seats, CancellationToken ct)
    {
        var delta = seats - enrollment.Seats;

        if (delta == 0)
        {
            return;
        }

        enrollment.Seats = seats;
        pass.RemainingVisits -= delta;

        await _unitOfWork.SaveChangesAsync(ct);
        await _cardCacheVersionService.IncrementAsync(enrollment.UserId, ct);
    }
}
