using WebApi.Contracts.Enrollments.Create;
using DataAccessLayer.Entities;
using WebApi.Contracts.Enrollments;

namespace WebApi.Extensions;

public static class EnrollmentMappingExtensions
{
    public static CreateEnrollmentResponse ToResponse(this Enrollment enrollment)
    {
        return new CreateEnrollmentResponse(
            enrollment.Id,
            enrollment.ClassId,
            enrollment.Seats,
            enrollment.Status,
            enrollment.CreatedAt);
    }

    private static EnrollmentRoomResponse ToRoomResponse(Room room)
    {
        return new EnrollmentRoomResponse(
            room.Id,
            room.Name);
    }

    private static EnrollmentPassResponse ToPassResponse(Pass pass)
    {
        return new EnrollmentPassResponse(
            pass.RemainingVisits,
            pass.ValidUntil);
    }

    private static EnrollmentClassResponse ToClassResponse(Class classEntity)
    {
        return new EnrollmentClassResponse(
            classEntity.Id,
            classEntity.Title,
            classEntity.StartsAt,
            classEntity.DurationMinutes,
            classEntity.IsCancelled,
            ToRoomResponse(classEntity.Room));
    }

    public static EnrollmentDetailsResponse ToDetailsResponse(
        this Enrollment enrollment,
        Pass pass)
    {
        return new EnrollmentDetailsResponse(
            enrollment.Id,
            enrollment.Status,
            enrollment.Seats,
            enrollment.CreatedAt,
            ToClassResponse(enrollment.Class),
            ToPassResponse(pass));
    }
}
