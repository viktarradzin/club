using Microsoft.AspNetCore.Mvc;
using WebApi.Contracts.Schedule;

namespace WebApi.Extensions;

public static class ScheduleResultExtensions
{
    public static IActionResult ToActionResult(this ScheduleResult result, ControllerBase controller)
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
