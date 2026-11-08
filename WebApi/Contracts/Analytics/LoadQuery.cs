using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebApi.ModelBinding;

namespace WebApi.Contracts.Analytics;

public class LoadQuery
{
    [Required]
    public string? From { get; init; }

    [Required]
    public string? To { get; init; }

    [ModelBinder(typeof(OptionalLongQueryModelBinder), Name = "roomId")]
    public long? RoomId { get; init; }
}
