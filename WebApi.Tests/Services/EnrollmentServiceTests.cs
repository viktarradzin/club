using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.Cancellation;
using WebApi.Contracts.Enrollments.SeatChanges;
using WebApi.Services.Enrollments;
using WebApi.Services.Waitlist;
using WebApi.Services.Caching;
using WebApi.Services.Transactions;
using Common.Caching;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Moq;
using WebApi.Contracts.Enrollments;

namespace WebApi.Tests.Services;

[TestClass]
public class EnrollmentServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IEnrollmentRepository> _enrollments = new();
    private readonly Mock<IPassRepository> _passes = new();
    private readonly Mock<IEnrollmentPreparationService> _preparation = new();
    private readonly Mock<IEnrollmentIdempotencyService> _idempotency = new();
    private readonly Mock<ITransactionExecutor> _transactions = new();
    private readonly Mock<ICardCacheVersionService> _cardCacheVersion = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<IWaitlistPromotionService> _promotion = new();
    private readonly CreateEnrollmentCommand _createCommand = new(7, 12, 2, "key");
    private ChangeEnrollmentSeatsCommand _seatChangeCommand = null!;
    private EnrollmentDetailsResponse? _cachedCard;
    private Enrollment? _enrollmentDetails;
    private Pass? _pass;
    private CreateEnrollmentResult? _previousResult;
    private EnrollmentPreparationResult _creationPreparation = null!;
    private SeatChangePreparationResult _seatChangePreparation = null!;
    private CancellationPreparationResult _cancellationPreparation = null!;
    private Exception? _saveException;
    private Enrollment? _createdEnrollment;
    private EnrollmentService _service = null!;

    [TestInitialize]
    public void SetUp()
    {
        _unitOfWork.SetupGet(x => x.Enrollment).Returns(_enrollments.Object);
        _unitOfWork.SetupGet(x => x.Pass).Returns(_passes.Object);
        _unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(() => _saveException is null ? Task.CompletedTask : Task.FromException(_saveException));
        _cardCacheVersion.Setup(x => x.IncrementAsync(7, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _cardCacheVersion.Setup(x => x.GetAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(4);
        _cache.Setup(x => x.GetAsync<EnrollmentDetailsResponse>("enrollment-card:v2:4:7:21", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _cachedCard);
        _enrollments.Setup(x => x.GetByIdForUserWithDetailsAsync(21, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _enrollmentDetails);
        _passes.Setup(x => x.GetByUserAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _pass);
        _idempotency.Setup(x => x.GetResultAsync(_createCommand, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _previousResult);
        _preparation.Setup(x => x.PrepareAsync(_createCommand, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _creationPreparation);
        _preparation.Setup(x => x.PrepareSeatChangeAsync(
                It.Is<ChangeEnrollmentSeatsCommand>(command => command == _seatChangeCommand),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _seatChangePreparation);
        _preparation.Setup(x => x.PrepareCancellationAsync(21, 7, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _cancellationPreparation);
        _enrollments.Setup(x => x.CreateAsync(It.IsAny<Enrollment>(), It.IsAny<CancellationToken>()))
            .Callback<Enrollment, CancellationToken>((enrollment, _) =>
            {
                enrollment.Id = 21;
                _createdEnrollment = enrollment;
            })
            .ReturnsAsync((Enrollment enrollment, CancellationToken _) => enrollment);
        _idempotency.Setup(x => x.StoreAsync(It.IsAny<CreateEnrollmentCommand>(), It.IsAny<CreateEnrollmentResponse>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _transactions.Setup(x => x.ExecuteAsync(It.IsAny<Func<CancellationToken, Task<CreateEnrollmentResult>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<CreateEnrollmentResult>> operation, CancellationToken ct) => operation(ct));
        _transactions.Setup(x => x.ExecuteAsync(It.IsAny<Func<CancellationToken, Task<ChangeEnrollmentSeatsResult>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<ChangeEnrollmentSeatsResult>> operation, CancellationToken ct) => operation(ct));
        _transactions.Setup(x => x.ExecuteAsync(It.IsAny<Func<CancellationToken, Task<CancelEnrollmentResult>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<CancelEnrollmentResult>> operation, CancellationToken ct) => operation(ct));
        _service = new EnrollmentService(_unitOfWork.Object, _preparation.Object, _idempotency.Object, _transactions.Object, _cardCacheVersion.Object, _cache.Object, _promotion.Object);
    }

    [TestMethod]
    public async Task GetDetailsAsync_WhenCardIsCached_ReturnsItWithoutReadingEnrollmentOrPass()
    {
        var enrollment = CreateEnrollment();
        var cached = new EnrollmentDetailsResponse(21, "active", 2, enrollment.CreatedAt,
            new EnrollmentClassResponse(12, "Class", enrollment.Class.StartsAt, 60, false,
                new EnrollmentRoomResponse(1, "Room")),
            new EnrollmentPassResponse(5, new DateOnly(2030, 1, 1)));
        _cachedCard = cached;

        var result = await _service.GetDetailsAsync(21, 7, CancellationToken.None);

        Assert.AreSame(cached, result);
        _enrollments.Verify(x => x.GetByIdForUserWithDetailsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _passes.Verify(x => x.GetByUserAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetDetailsAsync_WhenCacheMisses_LoadsAndCachesCard()
    {
        var enrollment = CreateEnrollment();
        var pass = new Pass { RemainingVisits = 5, ValidUntil = new DateOnly(2030, 1, 1) };
        _enrollmentDetails = enrollment;
        _pass = pass;

        var result = await _service.GetDetailsAsync(21, 7, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Seats);
        Assert.AreEqual(5, result.Pass.RemainingVisits);
        _cache.Verify(x => x.SetAsync("enrollment-card:v2:4:7:21", result, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetDetailsAsync_WhenEnrollmentIsNotFound_DoesNotCacheResponse()
    {
        var result = await _service.GetDetailsAsync(21, 7, CancellationToken.None);

        Assert.IsNull(result);
        _passes.Verify(x => x.GetByUserAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(x => x.SetAsync(It.IsAny<string>(), It.IsAny<EnrollmentDetailsResponse>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task CreateAsync_WhenPreviousResultExists_ReturnsItWithoutChanges()
    {
        var command = _createCommand;
        var previous = CreateEnrollmentResult.Success(new CreateEnrollmentResponse(21, 12, 2, "active", DateTime.UtcNow));
        _previousResult = previous;

        var result = await _service.CreateAsync(command, CancellationToken.None);

        Assert.AreSame(previous, result);
        _preparation.Verify(x => x.PrepareAsync(It.IsAny<CreateEnrollmentCommand>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _enrollments.Verify(x => x.CreateAsync(It.IsAny<Enrollment>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _cardCacheVersion.Verify(
            x => x.IncrementAsync(7, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task CreateAsync_WhenPreparationFails_ReturnsErrorWithoutChanges()
    {
        var command = _createCommand;
        _creationPreparation = EnrollmentPreparationResult.Failure(EnrollmentError.CapacityExceeded);

        var result = await _service.CreateAsync(command, CancellationToken.None);

        Assert.AreEqual(EnrollmentError.CapacityExceeded, result.Error);
        _enrollments.Verify(x => x.CreateAsync(It.IsAny<Enrollment>(), It.IsAny<CancellationToken>()), Times.Never);
        _idempotency.Verify(x => x.StoreAsync(It.IsAny<CreateEnrollmentCommand>(), It.IsAny<CreateEnrollmentResponse>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _cardCacheVersion.Verify(
            x => x.IncrementAsync(7, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task CreateAsync_WhenPrepared_CreatesEnrollmentDebitsPassAndStoresResponse()
    {
        var command = _createCommand;
        var pass = new Pass { RemainingVisits = 5 };
        _creationPreparation = EnrollmentPreparationResult.Success(pass);

        var result = await _service.CreateAsync(command, CancellationToken.None);

        var created = _createdEnrollment;
        Assert.IsNotNull(created);
        Assert.AreEqual(7L, created.UserId);
        Assert.AreEqual(12L, created.ClassId);
        Assert.AreEqual(2, created.Seats);
        Assert.AreEqual("active", created.Status);
        Assert.AreEqual(3, pass.RemainingVisits);
        Assert.IsNotNull(result.Response);
        Assert.AreEqual(21L, result.Response.Id);
        _idempotency.Verify(x => x.StoreAsync(command, result.Response, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _cardCacheVersion.Verify(
            x => x.IncrementAsync(7, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task CreateAsync_WhenSaveFails_PropagatesErrorAndDoesNotStoreResult()
    {
        var command = _createCommand;
        _creationPreparation = EnrollmentPreparationResult.Success(new Pass { RemainingVisits = 5 });
        _saveException = new InvalidOperationException("Save failed");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => _service.CreateAsync(command, CancellationToken.None));

        _idempotency.Verify(x => x.StoreAsync(It.IsAny<CreateEnrollmentCommand>(), It.IsAny<CreateEnrollmentResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ChangeSeatsAsync_WhenPreparationFails_ReturnsErrorWithoutSaving()
    {
        var command = new ChangeEnrollmentSeatsCommand(7, 21, 3);
        _seatChangeCommand = command;
        _seatChangePreparation = SeatChangePreparationResult.Failure(EnrollmentError.PassExhausted);

        var result = await _service.ChangeSeatsAsync(command, CancellationToken.None);

        Assert.AreEqual(EnrollmentError.PassExhausted, result.Error);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(4, 3, 1)]
    [DataRow(1, 6, 1)]
    [DataRow(2, 5, 0)]
    public async Task ChangeSeatsAsync_WhenPrepared_UpdatesPassByDifference(int newSeats, int expectedVisits, int saves)
    {
        var command = new ChangeEnrollmentSeatsCommand(7, 21, newSeats);
        _seatChangeCommand = command;
        var enrollment = CreateEnrollment();
        var pass = new Pass { RemainingVisits = 5, ValidUntil = new DateOnly(2030, 1, 1) };
        _seatChangePreparation = SeatChangePreparationResult.Success(enrollment, pass);

        var result = await _service.ChangeSeatsAsync(command, CancellationToken.None);

        Assert.IsNull(result.Error);
        Assert.AreEqual(newSeats, enrollment.Seats);
        Assert.AreEqual(expectedVisits, pass.RemainingVisits);
        Assert.IsNotNull(result.Response);
        Assert.AreEqual(newSeats, result.Response.Seats);
        Assert.AreEqual(expectedVisits, result.Response.Pass.RemainingVisits);
        Assert.AreEqual("Room", result.Response.Class.Room.Name);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(saves));
        _cardCacheVersion.Verify(
            x => x.IncrementAsync(7, It.IsAny<CancellationToken>()),
            Times.Exactly(saves));
    }

    [TestMethod]
    public async Task CancelAsync_WhenPreparationFails_ReturnsErrorWithoutSaving()
    {
        _cancellationPreparation = CancellationPreparationResult.Failure(EnrollmentError.ClassStarted);

        var result = await _service.CancelAsync(21, 7, CancellationToken.None);

        Assert.AreEqual(EnrollmentError.ClassStarted, result.Error);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _passes.Verify(x => x.GetByUserAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _cardCacheVersion.Verify(x => x.IncrementAsync(7, It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(2, 2)]
    [DataRow(null, 0)]
    public async Task CancelAsync_WhenAlreadyCancelled_ReturnsPreviousRefundWithoutChanges(int? storedRefund, int expectedRefund)
    {
        var enrollment = CreateEnrollment();
        enrollment.Status = "cancelled";
        enrollment.RefundedVisits = storedRefund;
        _cancellationPreparation = CancellationPreparationResult.Success(enrollment);

        var result = await _service.CancelAsync(21, 7, CancellationToken.None);

        Assert.AreEqual(new CancelEnrollmentResponse(21, "cancelled", expectedRefund), result.Response);
        _passes.Verify(x => x.GetByUserAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _cardCacheVersion.Verify(x => x.IncrementAsync(7, It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(180, false, 2)]
    [DataRow(30, false, 0)]
    [DataRow(-30, true, 2)]
    public async Task CancelAsync_WhenPrepared_UpdatesEnrollmentAndRefundsWhenApplicable(int startMinutes, bool classCancelled, int expectedRefund)
    {
        var enrollment = CreateEnrollment();
        enrollment.Class.StartsAt = DateTime.UtcNow.AddMinutes(startMinutes);
        enrollment.Class.IsCancelled = classCancelled;
        var pass = new Pass { RemainingVisits = 5 };
        _cancellationPreparation = CancellationPreparationResult.Success(enrollment);
        _pass = pass;
        var before = DateTime.UtcNow;

        var result = await _service.CancelAsync(21, 7, CancellationToken.None);

        var after = DateTime.UtcNow;
        Assert.AreEqual(new CancelEnrollmentResponse(21, "cancelled", expectedRefund), result.Response);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(expectedRefund, enrollment.RefundedVisits);
        Assert.IsTrue(enrollment.CancelledAt >= before && enrollment.CancelledAt <= after);
        Assert.AreEqual(5 + expectedRefund, pass.RemainingVisits);
        _passes.Verify(x => x.GetByUserAsync(7, It.IsAny<CancellationToken>()), Times.Exactly(expectedRefund > 0 ? 1 : 0));
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _cardCacheVersion.Verify(x => x.IncrementAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [DataTestMethod]
    [DataRow(-1, 0)]
    [DataRow(0, 2)]
    [DataRow(1, 2)]
    public async Task CancelAsync_AtTwoHourBoundary_RefundsInclusively(int offsetTicks, int expectedRefund)
    {
        var enrollment = CreateEnrollment();
        _pass = new Pass { RemainingVisits = 5 };
        _preparation.Setup(x => x.PrepareCancellationAsync(21, 7, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((long _, long _, DateTime now, CancellationToken _) =>
            {
                enrollment.Class.StartsAt = now.AddHours(2).AddTicks(offsetTicks);
                return Task.FromResult(CancellationPreparationResult.Success(enrollment));
            });

        var result = await _service.CancelAsync(21, 7, CancellationToken.None);

        Assert.AreEqual(new CancelEnrollmentResponse(21, "cancelled", expectedRefund), result.Response);
        Assert.AreEqual(5 + expectedRefund, _pass.RemainingVisits);
        Assert.AreEqual(expectedRefund, enrollment.RefundedVisits);
        Assert.AreEqual("cancelled", enrollment.Status);
        _promotion.Verify(x => x.PromoteAsync(12, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Enrollment CreateEnrollment() => new()
    {
        Id = 21, UserId = 7, ClassId = 12, Seats = 2, Status = "active", CreatedAt = DateTime.UtcNow,
        Class = new Class
        {
            Id = 12, Title = "Class", StartsAt = DateTime.UtcNow.AddHours(3), DurationMinutes = 60,
            Room = new Room { Id = 1, Name = "Room" }
        }
    };
}
