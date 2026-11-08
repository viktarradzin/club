using WebApi.Contracts.Enrollments.Create;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Enrollments;

public sealed class EnrollmentIdempotencyService : IEnrollmentIdempotencyService
{
    private const string Operation = "create-enrollment";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IUnitOfWork _unitOfWork;

    public EnrollmentIdempotencyService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<CreateEnrollmentResult?> GetResultAsync(
        CreateEnrollmentCommand command,
        CancellationToken ct)
    {
        // решение: ключ действует в пределах пользователя и операции,
        // чтобы одинаковые ключи разных пользователей и эндпоинтов не конфликтовали.
        var saved = await _unitOfWork.IdempotencyRequest.GetByKeyAsync(
            command.UserId, Operation, GetKeyHash(command.IdempotencyKey), command.IdempotencyKey, ct);

        if (saved is null)
        {
            return null;
        }

        // решение: при другом classId или seats возвращаем 409 idempotency-key-reused,
        // чтобы один ключ не обозначал две разные операции.
        if (saved.ClassId != command.ClassId || saved.Seats != command.Seats)
        {
            return CreateEnrollmentResult.Failure(EnrollmentError.IdempotencyKeyReused);
        }

        var response = JsonSerializer.Deserialize<CreateEnrollmentResponse>(saved.ResponseBody, SerializerOptions)
                       ?? throw new InvalidOperationException("Stored enrollment response is invalid.");

        return CreateEnrollmentResult.Success(response);
    }

    public async Task StoreAsync(
        CreateEnrollmentCommand command,
        CreateEnrollmentResponse response,
        CancellationToken ct)
    {
        var request = CreateRequest(command, response);

        await _unitOfWork.IdempotencyRequest.CreateAsync(request, ct);
    }

    private static string GetKeyHash(string key)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private static IdempotencyRequest CreateRequest(
        CreateEnrollmentCommand command,
        CreateEnrollmentResponse response)
    {
        return new IdempotencyRequest
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
    }
}
