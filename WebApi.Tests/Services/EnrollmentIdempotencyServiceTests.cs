using WebApi.Contracts.Enrollments.Create;
using WebApi.Services.Enrollments;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Moq;
using WebApi.Contracts.Enrollments;

namespace WebApi.Tests.Services;

[TestClass]
public class EnrollmentIdempotencyServiceTests
{
    private const string KeyHash = "42C8B95DC6A35B82EB9E4000244791509119ECEE347966E1AB6F08B31EAC1AFA";
    private readonly CreateEnrollmentCommand _command = new(7, 12, 2, "request-key");
    private readonly Mock<IIdempotencyRequestRepository> _repository = new(MockBehavior.Strict);
    private EnrollmentIdempotencyService _service = null!;
    private IdempotencyRequest? _savedRequest;
    private IdempotencyRequest? _storedRequest;

    [TestInitialize]
    public void SetUp()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(x => x.IdempotencyRequest).Returns(_repository.Object);
        _repository.Setup(x => x.GetByKeyAsync(7, "create-enrollment", KeyHash, "request-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _savedRequest);
        _repository.Setup(x => x.CreateAsync(It.IsAny<IdempotencyRequest>(), It.IsAny<CancellationToken>()))
            .Callback<IdempotencyRequest, CancellationToken>((request, _) => _storedRequest = request)
            .ReturnsAsync((IdempotencyRequest request, CancellationToken _) => request);
        _service = new EnrollmentIdempotencyService(unitOfWork.Object);
    }

    [TestMethod]
    public async Task GetResultAsync_WhenKeyIsNotFound_ReturnsNull()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var result = await _service.GetResultAsync(_command, token);

        Assert.IsNull(result);
        _repository.Verify(x => x.GetByKeyAsync(7, "create-enrollment", KeyHash, "request-key", token), Times.Once);
    }

    [TestMethod]
    public async Task GetResultAsync_WhenParametersMatch_ReturnsOriginalResponse()
    {
        _savedRequest = new IdempotencyRequest
        {
            ClassId = 12,
            Seats = 2,
            ResponseBody = "{\"id\":42,\"classId\":12,\"seats\":2,\"status\":\"active\",\"createdAt\":\"2026-11-06T10:00:00Z\"}"
        };
        var result = await _service.GetResultAsync(_command, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsNull(result.Error);
        Assert.AreEqual(new CreateEnrollmentResponse(42, 12, 2, "active", new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc)), result.Response);
    }

    [DataTestMethod]
    [DataRow(13L, 2)]
    [DataRow(12L, 3)]
    public async Task GetResultAsync_WhenKeyIsReusedWithDifferentParameters_ReturnsConflict(long classId, int seats)
    {
        _savedRequest = new IdempotencyRequest { ClassId = classId, Seats = seats };

        var result = await _service.GetResultAsync(_command, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(EnrollmentError.IdempotencyKeyReused, result.Error);
        Assert.IsNull(result.Response);
    }

    [TestMethod]
    public async Task GetResultAsync_WhenStoredResponseIsNull_Throws()
    {
        _savedRequest = new IdempotencyRequest { ClassId = 12, Seats = 2, ResponseBody = "null" };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => _service.GetResultAsync(_command, CancellationToken.None));
    }

    [TestMethod]
    public async Task StoreAsync_WhenCalled_StoresCommandAndOriginalResponse()
    {
        var createdAt = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
        var response = new CreateEnrollmentResponse(42, 12, 2, "active", createdAt);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        await _service.StoreAsync(_command, response, token);

        _repository.Verify(x => x.CreateAsync(It.IsAny<IdempotencyRequest>(), token), Times.Once);
        var stored = _storedRequest;
        Assert.IsNotNull(stored);
        Assert.AreEqual(7L, stored.UserId);
        Assert.AreEqual("create-enrollment", stored.Operation);
        Assert.AreEqual("request-key", stored.Key);
        Assert.AreEqual(KeyHash, stored.KeyHash);
        Assert.AreEqual(12L, stored.ClassId);
        Assert.AreEqual(2, stored.Seats);
        Assert.AreEqual(createdAt, stored.CreatedAt);
        var restored = JsonSerializer.Deserialize<CreateEnrollmentResponse>(stored.ResponseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.AreEqual(response, restored);
    }
}
