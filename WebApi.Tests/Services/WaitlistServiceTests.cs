using WebApi.Contracts.Enrollments.Waitlist;
using WebApi.Services.Waitlist;
using WebApi.Services.Transactions;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Moq;
using WebApi.Contracts.Enrollments;

namespace WebApi.Tests.Services;

[TestClass]
public class WaitlistServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new(MockBehavior.Strict);
    private readonly Mock<IWaitlistRepository> _waitlist = new(MockBehavior.Strict);
    private readonly Mock<ITransactionExecutor> _transactions = new(MockBehavior.Strict);
    private WaitlistService _service = null!;

    [TestInitialize]
    public void SetUp()
    {
        _unitOfWork.SetupGet(x => x.Waitlist).Returns(_waitlist.Object);
        _unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _transactions.Setup(x => x.ExecuteAsync(
                It.IsAny<Func<CancellationToken, Task<CancelWaitlistResult>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<CancelWaitlistResult>> operation, CancellationToken ct) => operation(ct));
        _service = new WaitlistService(_unitOfWork.Object, _transactions.Object,
            Mock.Of<IWaitlistIdempotencyService>(MockBehavior.Strict));
    }

    [TestMethod]
    public async Task CancelAsync_WhenEntryNotFound_DoesNotSaveChanges()
    {
        _waitlist.Setup(x => x.GetByIdForUserAsync(15, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WaitlistEntry?)null);

        var result = await _service.CancelAsync(15, 7, CancellationToken.None);

        Assert.AreEqual(EnrollmentError.WaitlistEntryNotFound, result.Error);
        Assert.IsNull(result.Response);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow("waiting", true)]
    [DataRow("fulfilled", true)]
    [DataRow("cancelled", false)]
    public async Task CancelAsync_WithDifferentStatuses_SavesOnlyWhenChanged(string status, bool saves)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var entry = new WaitlistEntry { Id = 15, UserId = 7, ClassId = 12, Seats = 2, Status = status };
        _waitlist.Setup(x => x.GetByIdForUserAsync(15, 7, token)).ReturnsAsync(entry);

        var result = await _service.CancelAsync(15, 7, token);

        Assert.IsNull(result.Error);
        Assert.AreEqual(new CancelWaitlistResponse(15, "cancelled"), result.Response);
        Assert.AreEqual("cancelled", entry.Status);
        Assert.AreEqual(2, entry.Seats);
        _unitOfWork.Verify(x => x.SaveChangesAsync(token), saves ? Times.Once() : Times.Never());
        _transactions.Verify(x => x.ExecuteAsync(
            It.IsAny<Func<CancellationToken, Task<CancelWaitlistResult>>>(), token), Times.Once);
    }
}
