using System.Security.Cryptography;
using System.Text.Json;
using DataAccessLayer.Models;

namespace WebApi.Services.Schedule;

public static class ScheduleCacheKey
{
    public static string Create(long version, SchedulePageQuery request)
    {
        var keyData = new
        {
            Version = version,
            request.UserId,
            FromTicks = request.From.Ticks,
            ToTicks = request.To.Ticks,
            request.RoomId,
            request.Take,
            AfterStartsAtTicks = request.AfterStartsAt?.Ticks,
            request.AfterClassId
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(keyData);
        var hash = SHA256.HashData(bytes);

        return $"schedule:v3:{Convert.ToHexString(hash)}";
    }
}
