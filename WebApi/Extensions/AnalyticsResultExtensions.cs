using Microsoft.AspNetCore.Mvc;
using WebApi.Contracts.Analytics;

namespace WebApi.Extensions;

public static class AnalyticsResultExtensions
{
    public static IActionResult ToActionResult(this LoadResult result, ControllerBase controller)
    {
        if (result.Error is not null)
        {
            return controller.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: result.Error);
        }

        return controller.Ok(result.Response);
    }
}
