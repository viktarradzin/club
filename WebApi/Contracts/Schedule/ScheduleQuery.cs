using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebApi.ModelBinding;

namespace WebApi.Contracts.Schedule;

public class ScheduleQuery
{
    [Required]
    public string? From { get; init; }

    [Required]
    public string? To { get; init; }

    [ModelBinder(typeof(OptionalLongQueryModelBinder), Name = "roomId")]
    public long? RoomId { get; init; }

    [Range(1, 100)]
    public int Limit { get; init; } = 20;

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? ContinuationToken { get; init; }
}
