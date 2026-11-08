namespace WebApi.Services.Waitlist;

public interface IWaitlistPromotionService
{
    Task PromoteAsync(long classId, DateTime now, CancellationToken ct);
}
