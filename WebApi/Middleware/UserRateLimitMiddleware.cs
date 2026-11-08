using System.Globalization;
using System.Security.Claims;
using Common.RateLimiting;
using WebApi.Attributes;

namespace WebApi.Middleware;

public sealed class UserRateLimitMiddleware
{
    private readonly RequestDelegate _next;

    public UserRateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IUserRateLimiter limiter)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<UserRateLimitAttribute>() is null)
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true ||
            !long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var decision = await limiter.TryAcquireAsync(userId, context.RequestAborted);
        if (decision.IsAllowed)
        {
            await _next(context);
            return;
        }

        context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: "The limit is 60 requests per user in 60 seconds.")
            .ExecuteAsync(context);
    }
}
