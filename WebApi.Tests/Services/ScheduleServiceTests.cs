using WebApi.Services.Caching;
using Common.Caching;
using DataAccessLayer.Models;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Moq;
using WebApi.Contracts.Schedule;
using WebApi.Services.Schedule;

namespace WebApi.Tests.Services;

[TestClass]
public class ScheduleServiceTests
{
    [TestMethod]
    public async Task GetAsync_OnCacheHit_SkipsRepository()
    {
        var uow = new Mock<IUnitOfWork>();
        var repository = new Mock<IClassRepository>();
        var cache = new Mock<ICacheService>();
        var versions = new Mock<ICardCacheVersionService>();
        var cached = new ScheduleResponse();
        uow.SetupGet(x => x.Class).Returns(repository.Object);
        versions.Setup(x => x.GetGlobalAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        cache.Setup(x => x.GetAsync<ScheduleResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);
        var service = new ScheduleService(uow.Object, cache.Object, versions.Object);

        var result = await service.GetAsync(new ScheduleQuery
        {
            From = "2030-01-01T10:00:00Z", To = "2030-01-01T12:00:00Z"
        }, 7);

        Assert.AreSame(cached, result.Response);
        repository.Verify(x => x.GetSchedulePageAsync(It.IsAny<SchedulePageQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetAsync_WithInvalidPeriod_DoesNotReadCacheOrDatabase()
    {
        var uow = new Mock<IUnitOfWork>();
        var cache = new Mock<ICacheService>();
        var versions = new Mock<ICardCacheVersionService>();
        var service = new ScheduleService(uow.Object, cache.Object, versions.Object);

        var result = await service.GetAsync(new ScheduleQuery
        {
            From = "2030-01-01T10:00:00Z", To = "2030-01-01T10:00:00Z"
        }, 7);

        Assert.IsNotNull(result.Error);
        versions.Verify(x => x.GetGlobalAsync(It.IsAny<CancellationToken>()), Times.Never);
        cache.Verify(x => x.GetAsync<ScheduleResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
