using Microsoft.AspNetCore.WebUtilities;
using System.Text;
using WebApi.Contracts.Schedule;
using WebApi.Services.Schedule;

namespace WebApi.Tests.Services;

[TestClass]
public class ScheduleTokenTests
{
    [TestMethod]
    public void Token_WithPositionAndFilters_RoundTripsValues()
    {
        var start = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var original = new ScheduleContinuation
        {
            Version = 1, From = start, To = start.AddHours(5), RoomId = 12,
            LastStartsAt = start.AddHours(1), LastClassId = 42
        };

        var token = ScheduleContinuationTokenService.Encode(original);

        Assert.IsTrue(ScheduleContinuationTokenService.TryDecode(token, out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(original.From, parsed.From);
        Assert.AreEqual(original.To, parsed.To);
        Assert.AreEqual(original.RoomId, parsed.RoomId);
        Assert.AreEqual(original.LastStartsAt, parsed.LastStartsAt);
        Assert.AreEqual(original.LastClassId, parsed.LastClassId);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("not!base64")]
    [DataRow("e30")]
    public void Token_WithInvalidInput_IsRejected(string token)
    {
        Assert.IsFalse(ScheduleContinuationTokenService.TryDecode(token, out var parsed));
        Assert.IsNull(parsed);
    }

    [TestMethod]
    public void Token_WithInvalidJson_IsRejected()
    {
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("{"));
        Assert.IsFalse(ScheduleContinuationTokenService.TryDecode(token, out _));
    }
}
