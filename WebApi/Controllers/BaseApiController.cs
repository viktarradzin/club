using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WebApi.Constants;

namespace WebApi.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = "admin";

    protected string IdempotencyKey =>
        Request.Headers[ApiHeaders.IdempotencyKey].ToString();

    protected long CurrentUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    protected bool IsAdmin => User.IsInRole(AdminRole);
}
