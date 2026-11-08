using WebApi.Contracts.Enrollments.Waitlist;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Waitlist;

public sealed class WaitlistIdempotencyService : IWaitlistIdempotencyService
{
    private const string Operation = "join-waitlist";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IUnitOfWork _unitOfWork;

    public WaitlistIdempotencyService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<JoinWaitlistResult?> GetResultAsync(
        JoinWaitlistCommand command,
        CancellationToken ct)
    {
        var saved = await _unitOfWork.IdempotencyRequest.GetByKeyAsync(
            command.UserId,
            Operation,
            GetKeyHash(command.IdempotencyKey),
            command.IdempotencyKey,
            ct);

        if (saved is null)
        {
            return null;
        }

        if (saved.ClassId != command.ClassId || saved.Seats != command.Seats)
        {
            return JoinWaitlistResult.Failure(
                EnrollmentError.IdempotencyKeyReused);
        }

        var response = JsonSerializer.Deserialize<JoinWaitlistResponse>(
                           saved.ResponseBody,
                           SerializerOptions)
                       ?? throw new InvalidOperationException(
                           "Stored waitlist response is invalid.");

        return JoinWaitlistResult.Success(response);
    }

    public async Task StoreAsync(
        JoinWaitlistCommand command,
        JoinWaitlistResponse response,
        CancellationToken ct)
    {
        var request = new IdempotencyRequest
        {
            UserId = command.UserId,
            Operation = Operation,
            Key = command.IdempotencyKey,
            KeyHash = GetKeyHash(command.IdempotencyKey),
            ClassId = command.ClassId,
            Seats = command.Seats,
            ResponseBody = JsonSerializer.Serialize(response, SerializerOptions),
            CreatedAt = response.CreatedAt
        };

        await _unitOfWork.IdempotencyRequest.CreateAsync(request, ct);
    }

    private static string GetKeyHash(string key)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}
