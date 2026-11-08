using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Attributes;
using WebApi.Contracts.Analytics;
using WebApi.Extensions;
using WebApi.Services.Analytics;

namespace WebApi.Controllers;

[ApiController]
[Authorize(Roles = "admin")]
[UserRateLimit]
[Route("analytics")]
public class AnalyticsController : BaseApiController
{
    private readonly IAnalyticsService _analyticsService;

    public AnalyticsController(IAnalyticsService analyticsService)
    {
        _analyticsService = analyticsService;
    }

    [HttpGet("load")]
    public async Task<IActionResult> GetLoad([FromQuery] LoadQuery query, CancellationToken ct)
    {
        var result = await _analyticsService.GetLoadAsync(query, ct);
        return result.ToActionResult(this);
    }
}
