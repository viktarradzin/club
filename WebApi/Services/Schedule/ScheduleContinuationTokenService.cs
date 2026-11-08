using System.Text.Json;
using WebApi.Contracts.Schedule;
using Microsoft.AspNetCore.WebUtilities;

namespace WebApi.Services.Schedule;

public static class ScheduleContinuationTokenService
{
    private const int CurrentVersion = 1;

    public static string Encode(ScheduleContinuation continuation)
    {
        // решение: кодируем JSON с версией формата в Base64Url,
        // чтобы передавать позицию страницы и фильтры одним параметром URL.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(continuation);

        return WebEncoders.Base64UrlEncode(bytes);
    }

    public static bool TryDecode(
        string token,
        out ScheduleContinuation? continuation)
    {
        continuation = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            var decoded = JsonSerializer.Deserialize<ScheduleContinuation>(bytes);

            if (!IsValid(decoded))
            {
                return false;
            }

            continuation = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValid(ScheduleContinuation? continuation)
    {
        return continuation is not null
               && continuation.Version == CurrentVersion
               && continuation.From.Kind == DateTimeKind.Utc
               && continuation.To.Kind == DateTimeKind.Utc
               && continuation.LastStartsAt.Kind == DateTimeKind.Utc
               && continuation.To > continuation.From
               && continuation.LastStartsAt >= continuation.From
               && continuation.LastStartsAt < continuation.To
               && continuation.LastClassId > 0;
    }
}
