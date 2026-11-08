using WebApi.Contracts.Enrollments.Create;
using WebApi.Contracts.Enrollments.SeatChanges;
using WebApi.Contracts.Enrollments.Waitlist;
using WebApi.Services.Enrollments;
using WebApi.Services.Waitlist;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Attributes;
using WebApi.Extensions;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[UserRateLimit]
[Route("enrollments")]
public class EnrollmentsController : BaseApiController
{
    private readonly IEnrollmentService _enrollmentService;
    private readonly IWaitlistService _waitlistService;

    public EnrollmentsController(
        IEnrollmentService enrollmentService,
        IWaitlistService waitlistService)
    {
        _enrollmentService = enrollmentService;
        _waitlistService = waitlistService;
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateEnrollmentRequest request,
        [FromIdempotencyKey, Required] string? key,
        CancellationToken ct)
    {
        var command = request.ToCommand(CurrentUserId, key!);
        var result = await _enrollmentService.CreateAsync(command, ct);

        return result.ToActionResult(this);
    }

    [HttpPost("{id}/seats")]
    public async Task<IActionResult> ChangeSeats(
        long id,
        ChangeEnrollmentSeatsRequest request,
        CancellationToken ct)
    {
        var command = request.ToCommand(CurrentUserId, id);
        var result = await _enrollmentService.ChangeSeatsAsync(command, ct);

        return result.ToActionResult(this);
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        var result = await _enrollmentService.CancelAsync(id, CurrentUserId, ct);

        return result.ToActionResult(this);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(long id, CancellationToken ct)
    {
        var response = await _enrollmentService.GetDetailsAsync(id, CurrentUserId, ct);

        return response.ToActionResult(this);
    }

    [HttpPost("waitlist")]
    public async Task<IActionResult> JoinWaitlist(
        JoinWaitlistRequest request,
        [FromIdempotencyKey, Required] string? key,
        CancellationToken ct)
    {
        var command = request.ToCommand(CurrentUserId, key!);
        var result = await _waitlistService.JoinAsync(command, ct);

        return result.ToActionResult(this);
    }

    [HttpPost("waitlist/{id}/cancel")]
    public async Task<IActionResult> CancelWaitlist(long id, CancellationToken ct)
    {
        var result = await _waitlistService.CancelAsync(id, CurrentUserId, ct);
        return result.ToActionResult(this);
    }
}
