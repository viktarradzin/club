using System.ComponentModel.DataAnnotations;

namespace WebApi.Contracts.Enrollments.Create;

public sealed class CreateEnrollmentRequest
{
    [Required]
    public long? ClassId { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; init; }
}
