using WebApi.Services.Enrollments;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Services.Caching;

namespace WebApi.Services.Waitlist;

public sealed class WaitlistPromotionService : IWaitlistPromotionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardCacheVersionService _cacheVersionService;

    public WaitlistPromotionService(IUnitOfWork unitOfWork, ICardCacheVersionService cacheVersionService)
    {
        _unitOfWork = unitOfWork;
        _cacheVersionService = cacheVersionService;
    }

    public async Task PromoteAsync(long classId, DateTime now, CancellationToken ct)
    {
        var classEntity = await _unitOfWork.Class.GetByIdIncludingCancelledAsync(classId, ct)
            ?? throw new InvalidOperationException("Class was not found.");

        if (classEntity.IsCancelled || classEntity.StartsAt <= now)
        {
            return;
        }

        var occupiedSeats = await _unitOfWork.Enrollment.GetOccupiedSeatsAsync(classId, ct);
        var availableSeats = classEntity.Capacity - occupiedSeats;

        while (availableSeats > 0)
        {
            var entry = await _unitOfWork.Waitlist.GetFirstWaitingAsync(classId, ct);
            if (entry is null)
            {
                return;
            }

            // решение: выдаём заявку только целиком; при нехватке мест останавливаем
            // очередь, чтобы более поздние заявки не обходили первую.
            if (entry.Seats > availableSeats)
            {
                return;
            }

            var pass = await _unitOfWork.Pass.GetByUserAsync(entry.UserId, ct)
                ?? throw new InvalidOperationException("User pass was not found.");

            // решение: если абонемент первого клиента не позволяет выдать места,
            // останавливаем очередь, сохраняя его позицию до следующей попытки.
            if (EnrollmentRules.GetPassError(pass, entry.Seats, DateOnly.FromDateTime(now)) is not null)
            {
                return;
            }

            var enrollment = new Enrollment
            {
                UserId = entry.UserId,
                ClassId = entry.ClassId,
                Seats = entry.Seats,
                Status = "active",
                CreatedAt = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc)
            };

            await _unitOfWork.Enrollment.CreateAsync(enrollment, ct);
            pass.RemainingVisits -= entry.Seats;
            entry.Status = "fulfilled";
            await _unitOfWork.SaveChangesAsync(ct);
            await _cacheVersionService.IncrementAsync(entry.UserId, ct);

            availableSeats -= entry.Seats;
        }
    }
}
