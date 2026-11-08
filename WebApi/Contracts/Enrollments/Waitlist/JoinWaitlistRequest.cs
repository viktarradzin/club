using System.ComponentModel.DataAnnotations;

namespace WebApi.Contracts.Enrollments.Waitlist;

public sealed class JoinWaitlistRequest
{
    [Required]
    public long? ClassId { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; init; }
}
