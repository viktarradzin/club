using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Attributes;
using WebApi.Contracts.Schedule;
using WebApi.Extensions;
using WebApi.Services.Schedule;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[UserRateLimit]
[Route("schedule")]
public class ScheduleController : BaseApiController
{
    private readonly IScheduleService _scheduleService;

    public ScheduleController(IScheduleService scheduleService)
    {
        _scheduleService = scheduleService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] ScheduleQuery query, CancellationToken ct)
    {
        var result = await _scheduleService.GetAsync(query, CurrentUserId, ct);
        return result.ToActionResult(this);
    }
}
