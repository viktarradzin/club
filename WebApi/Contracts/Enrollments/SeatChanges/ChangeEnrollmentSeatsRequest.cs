using System.ComponentModel.DataAnnotations;

namespace WebApi.Contracts.Enrollments.SeatChanges;

public sealed class ChangeEnrollmentSeatsRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; init; }
}
