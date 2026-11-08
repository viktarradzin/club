using WebApi.Services.Enrollments;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Moq;
using WebApi.Contracts.Enrollments;

namespace WebApi.Tests.Services;

[TestClass]
public class EnrollmentPreparationServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IClassRepository> _classes = new();
    private readonly Mock<IEnrollmentRepository> _enrollments = new();
    private readonly Mock<IPassRepository> _passes = new();
    private EnrollmentPreparationService _service = null!;

    [TestInitialize]
    public void SetUp()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(x => x.Class).Returns(_classes.Object);
        unitOfWork.SetupGet(x => x.Enrollment).Returns(_enrollments.Object);
        unitOfWork.SetupGet(x => x.Pass).Returns(_passes.Object);
        _service = new EnrollmentPreparationService(unitOfWork.Object);
    }

    [TestMethod]
    public async Task PrepareAsync_WhenClassDoesNotExist_ReturnsNotFound()
    {
        var result = await _service.PrepareAsync(new(7, 12, 2, "key"), Now, CancellationToken.None);

        Assert.AreEqual(EnrollmentError.ClassNotFound, result.Error);
        _passes.Verify(x => x.GetByUserAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(true, 60, EnrollmentError.ClassCancelled)]
    [DataRow(false, 0, EnrollmentError.ClassStarted)]
    [DataRow(false, -1, EnrollmentError.ClassStarted)]
    public async Task PrepareAsync_WhenClassIsUnavailable_ReturnsError(bool cancelled, int minutes, EnrollmentError expected)
    {
        SetupClass(new Class { Id = 12, IsCancelled = cancelled, StartsAt = Now.AddMinutes(minutes), Capacity = 10 });

        var result = await _service.PrepareAsync(new(7, 12, 2, "key"), Now, CancellationToken.None);

        Assert.AreEqual(expected, result.Error);
        _passes.Verify(x => x.GetByUserAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task PrepareAsync_WhenPassDoesNotExist_Throws()
    {
        SetupClass(AvailableClass());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => _service.PrepareAsync(new(7, 12, 2, "key"), Now, CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow(-1, 10, EnrollmentError.PassExpired)]
    [DataRow(0, 1, EnrollmentError.PassExhausted)]
    public async Task PrepareAsync_WhenPassCannotCoverSeats_ReturnsError(int validDays, int visits, EnrollmentError expected)
    {
        SetupClass(AvailableClass());
        SetupPass(new Pass { ValidUntil = DateOnly.FromDateTime(Now).AddDays(validDays), RemainingVisits = visits });

        var result = await _service.PrepareAsync(new(7, 12, 2, "key"), Now, CancellationToken.None);

        Assert.AreEqual(expected, result.Error);
        _enrollments.Verify(x => x.GetOccupiedSeatsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(8L, false)]
    [DataRow(9L, true)]
    public async Task PrepareAsync_WhenCheckingCapacity_ReturnsExpectedResult(long occupied, bool exceedsCapacity)
    {
        SetupClass(AvailableClass());
        var pass = new Pass { ValidUntil = DateOnly.FromDateTime(Now), RemainingVisits = 2 };
        SetupPass(pass);
        SetupOccupied(occupied);

        var result = await _service.PrepareAsync(new(7, 12, 2, "key"), Now, CancellationToken.None);

        Assert.AreEqual(exceedsCapacity ? EnrollmentError.CapacityExceeded : (EnrollmentError?)null, result.Error);
        if (!exceedsCapacity) Assert.AreSame(pass, result.Pass);
    }

    [TestMethod]
    public async Task PrepareSeatChangeAsync_WhenEnrollmentIsMissing_ReturnsNotFound()
    {
        var result = await _service.PrepareSeatChangeAsync(new(7, 21, 3), Now, CancellationToken.None);

        Assert.AreEqual(EnrollmentError.EnrollmentNotFound, result.Error);
    }

    [DataTestMethod]
    [DataRow("cancelled", false, 60, EnrollmentError.EnrollmentCancelled)]
    [DataRow("active", true, 60, EnrollmentError.ClassCancelled)]
    [DataRow("active", false, 0, EnrollmentError.ClassStarted)]
    public async Task PrepareSeatChangeAsync_WhenEnrollmentOrClassIsUnavailable_ReturnsError(
        string status, bool classCancelled, int minutes, EnrollmentError expected)
    {
        var enrollment = AvailableEnrollment();
        enrollment.Status = status;
        enrollment.Class.IsCancelled = classCancelled;
        enrollment.Class.StartsAt = Now.AddMinutes(minutes);
        SetupEnrollment(enrollment);

        var result = await _service.PrepareSeatChangeAsync(new(7, 21, 2), Now, CancellationToken.None);

        Assert.AreEqual(expected, result.Error);
        _passes.Verify(x => x.GetByUserAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task PrepareSeatChangeAsync_WhenSeatsDecreaseOrStaySame_DoesNotCheckPassOrCapacity(int seats)
    {
        var enrollment = AvailableEnrollment();
        var pass = new Pass { ValidUntil = DateOnly.FromDateTime(Now).AddDays(-1), RemainingVisits = 0 };
        SetupEnrollment(enrollment);
        SetupPass(pass);

        var result = await _service.PrepareSeatChangeAsync(new(7, 21, seats), Now, CancellationToken.None);

        Assert.IsNull(result.Error);
        Assert.AreSame(enrollment, result.Enrollment);
        Assert.AreSame(pass, result.Pass);
        _enrollments.Verify(x => x.GetOccupiedSeatsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(-1, 10, 0L, EnrollmentError.PassExpired)]
    [DataRow(1, 0, 0L, EnrollmentError.PassExhausted)]
    [DataRow(1, 1, 10L, EnrollmentError.CapacityExceeded)]
    public async Task PrepareSeatChangeAsync_WhenIncreaseIsNotPossible_ReturnsError(
        int validDays, int visits, long occupied, EnrollmentError expected)
    {
        SetupEnrollment(AvailableEnrollment());
        SetupPass(new Pass { ValidUntil = DateOnly.FromDateTime(Now).AddDays(validDays), RemainingVisits = visits });
        SetupOccupied(occupied);

        var result = await _service.PrepareSeatChangeAsync(new(7, 21, 3), Now, CancellationToken.None);

        Assert.AreEqual(expected, result.Error);
    }

    [TestMethod]
    public async Task PrepareSeatChangeAsync_WhenIncreaseFits_ReturnsEnrollmentAndPass()
    {
        var enrollment = AvailableEnrollment();
        var pass = new Pass { ValidUntil = DateOnly.FromDateTime(Now), RemainingVisits = 1 };
        SetupEnrollment(enrollment);
        SetupPass(pass);
        SetupOccupied(9);

        var result = await _service.PrepareSeatChangeAsync(new(7, 21, 3), Now, CancellationToken.None);

        Assert.IsNull(result.Error);
        Assert.AreSame(enrollment, result.Enrollment);
        Assert.AreSame(pass, result.Pass);
    }

    [TestMethod]
    public async Task PrepareSeatChangeAsync_WhenPassIsMissing_Throws()
    {
        SetupEnrollment(AvailableEnrollment());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => _service.PrepareSeatChangeAsync(new(7, 21, 3), Now, CancellationToken.None));
    }

    [TestMethod]
    public async Task PrepareCancellationAsync_WhenEnrollmentIsMissing_ReturnsNotFound()
    {
        var result = await _service.PrepareCancellationAsync(21, 7, Now, CancellationToken.None);

        Assert.AreEqual(EnrollmentError.EnrollmentNotFound, result.Error);
    }

    [DataTestMethod]
    [DataRow("active", false, 0, false)]
    [DataRow("active", false, -1, false)]
    [DataRow("active", false, 1, true)]
    [DataRow("active", true, -1, true)]
    [DataRow("cancelled", false, -1, true)]
    public async Task PrepareCancellationAsync_WithDifferentStates_ChecksStatusAndClassStart(
        string status, bool classCancelled, int minutes, bool allowed)
    {
        var enrollment = AvailableEnrollment();
        enrollment.Status = status;
        enrollment.Class.IsCancelled = classCancelled;
        enrollment.Class.StartsAt = Now.AddMinutes(minutes);
        SetupEnrollment(enrollment);

        var result = await _service.PrepareCancellationAsync(21, 7, Now, CancellationToken.None);

        Assert.AreEqual(allowed ? (EnrollmentError?)null : EnrollmentError.ClassStarted, result.Error);
        if (allowed) Assert.AreSame(enrollment, result.Enrollment);
    }

    private void SetupClass(Class classEntity) => _classes
        .Setup(x => x.GetByIdIncludingCancelledAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(classEntity);

    private void SetupEnrollment(Enrollment enrollment) => _enrollments
        .Setup(x => x.GetByIdForUserWithDetailsAsync(21, 7, It.IsAny<CancellationToken>())).ReturnsAsync(enrollment);

    private void SetupPass(Pass pass) => _passes
        .Setup(x => x.GetByUserAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(pass);

    private void SetupOccupied(long occupied) => _enrollments
        .Setup(x => x.GetOccupiedSeatsAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(occupied);

    private static Class AvailableClass() => new() { Id = 12, StartsAt = Now.AddHours(3), Capacity = 10 };

    private static Enrollment AvailableEnrollment() => new()
    {
        Id = 21, UserId = 7, ClassId = 12, Seats = 2, Status = "active", Class = AvailableClass()
    };
}
