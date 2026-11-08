using WebApi.Contracts.Enrollments.Create;
using DataAccessLayer.Entities;
using WebApi.Contracts.Enrollments;

namespace WebApi.Services.Enrollments;

internal static class EnrollmentFactory
{
    public static Enrollment Create(
        CreateEnrollmentCommand command,
        DateTime createdAt)
    {
        return new Enrollment
        {
            UserId = command.UserId,
            ClassId = command.ClassId,
            Seats = command.Seats,
            Status = "active",
            CreatedAt = createdAt
        };
    }
}
