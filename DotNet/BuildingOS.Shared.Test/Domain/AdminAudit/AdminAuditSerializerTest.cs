using BuildingOS.Shared.Domain.AdminAudit;

namespace BuildingOS.Shared.Test.Domain.AdminAudit;

/// <summary>
/// Pure unit tests for <see cref="AdminAuditSerializer"/> and the <see cref="AdminAuditRecord"/>
/// factory. Real DB round-trips live in BuildingOS.IntegrationTest (Testcontainers).
/// </summary>
public class AdminAuditSerializerTest
{
    private static AdminAuditRecord SampleRecord(
        AdminAuditResult result = AdminAuditResult.Success,
        string? detail = """{"mode":"replace","rows":42}""")
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var ts = new DateTime(2026, 6, 17, 1, 2, 3, DateTimeKind.Utc);
        return new AdminAuditRecord(
            id, AdminAuditSubjects.Twin, "import", "GW001", "admin-sub", "Demo Admin", result, detail, ts);
    }

    [Fact]
    public void ToEntry_MapsAllFields()
    {
        var record = SampleRecord();

        var entry = AdminAuditSerializer.ToEntry(record);

        Assert.Equal(record.Id, entry.Id);
        Assert.Equal("twin", entry.SubjectType);
        Assert.Equal("import", entry.Action);
        Assert.Equal("GW001", entry.TargetId);
        Assert.Equal("admin-sub", entry.ActorSub);
        Assert.Equal("Demo Admin", entry.ActorName);
        Assert.Equal("success", entry.Result);
        Assert.Equal(record.DetailJson, entry.Detail);
        Assert.Equal(record.CreatedAt, entry.CreatedAt);
    }

    [Fact]
    public void RoundTrip_PreservesRecord()
    {
        var record = SampleRecord(AdminAuditResult.Failure, detail: null);

        var roundTripped = AdminAuditSerializer.ToDomain(AdminAuditSerializer.ToEntry(record));

        Assert.Equal(record, roundTripped);
    }

    [Theory]
    [InlineData(AdminAuditResult.Success, "success")]
    [InlineData(AdminAuditResult.Failure, "failure")]
    public void ToResultText_UsesLowercaseTokens(AdminAuditResult result, string expected)
    {
        Assert.Equal(expected, AdminAuditSerializer.ToResultText(result));
    }

    [Theory]
    [InlineData("failure", AdminAuditResult.Failure)]
    [InlineData("FAILURE", AdminAuditResult.Failure)]
    [InlineData("success", AdminAuditResult.Success)]
    [InlineData("", AdminAuditResult.Success)]
    [InlineData(null, AdminAuditResult.Success)]
    [InlineData("garbage", AdminAuditResult.Success)]
    public void ParseResult_IsCaseInsensitiveAndDefaultsToSuccess(string? text, AdminAuditResult expected)
    {
        Assert.Equal(expected, AdminAuditSerializer.ParseResult(text));
    }

    [Fact]
    public void Create_AssignsIdAndTimestamp()
    {
        var before = DateTime.UtcNow;

        var record = AdminAuditRecord.Create(
            AdminAuditSubjects.OidcClient, "rotate-secret", "client-1",
            "admin-sub", "Demo Admin", AdminAuditResult.Success, null);

        Assert.NotEqual(Guid.Empty, record.Id);
        Assert.InRange(record.CreatedAt, before, DateTime.UtcNow);
        Assert.Equal("oidc-client", record.SubjectType);
        Assert.Equal("rotate-secret", record.Action);
        Assert.Equal("client-1", record.TargetId);
    }
}
