using BuildingOS.Shared.Infrastructure.PointControlRepository;

namespace BuildingOS.Shared.Test.Infrastructure.PointControl;

public class ControlAuditCursorTest
{
    [Fact]
    public void RoundTrips_ToTheTick()
    {
        var cursor = new ControlAuditCursor(new DateTime(2026, 9, 29, 10, 5, 0, DateTimeKind.Utc).AddTicks(1234567), Guid.NewGuid());

        Assert.Equal(cursor, ControlAuditCursor.TryDecode(cursor.Encode()));
    }

    [Fact]
    public void Encode_IsUrlSafe()
    {
        var encoded = new ControlAuditCursor(DateTime.UtcNow, Guid.NewGuid()).Encode();

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("eyJ9")]                    // "{}" — well-formed JSON, missing fields
    [InlineData("eyJ0IjoieCIsImlkIjoieSJ9")] // {"t":"x","id":"y"} — wrong field formats
    public void TryDecode_ReturnsNull_ForAnythingItDidNotEncode(string raw)
        => Assert.Null(ControlAuditCursor.TryDecode(raw));
}
