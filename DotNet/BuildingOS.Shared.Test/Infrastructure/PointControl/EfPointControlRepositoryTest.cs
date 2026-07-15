using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.PointControl;
using System.Text.Json;

namespace BuildingOS.Shared.Test.Infrastructure.PointControl;

/// <summary>
/// Pure unit tests for PointControlAuditEntry and PointControlAuditSerializer.
/// Real DB tests are in BuildingOS.IntegrationTest (Testcontainers).
/// </summary>
public class EfPointControlRepositoryTest
{
    // ─── PointControlAuditEntry プロパティ ───────────────────────────────────

    [Fact]
    public void PointControlAuditEntry_DefaultsAreEmpty()
    {
        var entry = new PointControlAuditEntry();
        Assert.Equal(Guid.Empty, entry.Id);
        Assert.Null(entry.PointId);
        Assert.Equal("", entry.Request);
        Assert.Null(entry.Result);
        Assert.Equal(default(DateTime), entry.CreatedAt);
        Assert.Null(entry.CompletedAt);
    }

    [Fact]
    public void PointControlAuditEntry_PropertiesCanBeSet()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var entry = new PointControlAuditEntry
        {
            Id = id,
            PointId = "point-1",
            Request = """{"type":"Kandt","pointId":"point-1"}""",
            Result = """{"status":"success","response":"{}"}""",
            CreatedAt = now,
            CompletedAt = now.AddSeconds(1)
        };

        Assert.Equal(id, entry.Id);
        Assert.Equal("point-1", entry.PointId);
        Assert.Contains("Kandt", entry.Request);
        Assert.Contains("success", entry.Result!);
        Assert.Equal(now, entry.CreatedAt);
        Assert.NotNull(entry.CompletedAt);
    }

    // ─── PointControlAuditSerializer: ToEntry ──────────────────────────────

    [Fact]
    public void ToEntry_SetsIdAndRequest_FromPointControlInfo()
    {
        var id = Guid.NewGuid();
        var body = """{"type":"Kandt","pointId":"p-1","objectId":42}""";
        var info = new PointControlInfo { id = id, Type = "Kandt", Body = body, PointId = "p-1" };

        var entry = PointControlAuditSerializer.ToEntry(info);

        Assert.Equal(id, entry.Id);
        Assert.Equal("p-1", entry.PointId);
        Assert.Equal(body, entry.Request);
    }

    [Fact]
    public void ToEntry_ExtractsPointId_FromBodyWhenPropertyIsNull()
    {
        var body = """{"type":"Hono","pointId":"extracted-point"}""";
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Hono", Body = body, PointId = null };

        var entry = PointControlAuditSerializer.ToEntry(info);

        Assert.Equal("extracted-point", entry.PointId);
    }

    [Fact]
    public void ToEntry_PointIdFallsBackToEmptyString_WhenAbsent()
    {
        // #235 review: back-compat with the prior Npgsql writer — no point id ⇒ "" (not null),
        // so existing point_id-based queries / aggregates / index selectivity are unchanged.
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Kandt", Body = "{}", PointId = null };

        var entry = PointControlAuditSerializer.ToEntry(info);

        Assert.Equal(string.Empty, entry.PointId);
    }

    [Fact]
    public void ToEntry_ResultIsNull_BeforeUpdate()
    {
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Kandt", Body = "{}" };

        var entry = PointControlAuditSerializer.ToEntry(info);

        Assert.Null(entry.Result);
        Assert.Null(entry.CompletedAt);
    }

    // ─── PointControlAuditSerializer: ApplyResult ──────────────────────────

    [Fact]
    public void ApplyResult_SetsResultJson_ForSuccess()
    {
        var entry = new PointControlAuditEntry { Id = Guid.NewGuid(), Request = "{}" };
        var info = new PointControlInfo
        {
            id = entry.Id,
            Type = "Kandt",
            Body = "{}",
            Result = PointControlResult.Success,
            Response = """{"code":0}"""
        };

        PointControlAuditSerializer.ApplyResult(entry, info);

        Assert.NotNull(entry.Result);
        Assert.NotNull(entry.CompletedAt);
        var doc = JsonDocument.Parse(entry.Result!);
        Assert.Equal("success", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("""{"code":0}""", doc.RootElement.GetProperty("response").GetString());
    }

    [Fact]
    public void ApplyResult_SetsResultJson_ForFailed()
    {
        var entry = new PointControlAuditEntry { Id = Guid.NewGuid(), Request = "{}" };
        var info = new PointControlInfo
        {
            id = entry.Id,
            Type = "Kandt",
            Body = "{}",
            Result = PointControlResult.Failed,
            Response = "timeout"
        };

        PointControlAuditSerializer.ApplyResult(entry, info);

        var doc = JsonDocument.Parse(entry.Result!);
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ApplyResult_NullResultInfo_LeavesResultNull()
    {
        var entry = new PointControlAuditEntry { Id = Guid.NewGuid(), Request = "{}" };
        var info = new PointControlInfo { id = entry.Id, Type = "Kandt", Body = "{}", Result = null };

        PointControlAuditSerializer.ApplyResult(entry, info);

        Assert.Null(entry.Result);
    }

    // ─── PointControlAuditSerializer: ToDomain ─────────────────────────────

    [Fact]
    public void ToDomain_RestoresIdBodyAndType()
    {
        var id = Guid.NewGuid();
        var body = """{"type":"BacnetSim","pointId":"p-42"}""";
        var entry = new PointControlAuditEntry
        {
            Id = id,
            PointId = "p-42",
            Request = body,
            CreatedAt = DateTime.UtcNow
        };

        var info = PointControlAuditSerializer.ToDomain(entry);

        Assert.Equal(id, info.id);
        Assert.Equal(body, info.Body);
        Assert.Equal("BacnetSim", info.Type);
    }

    [Fact]
    public void ToDomain_ParsesSuccessResult()
    {
        var entry = new PointControlAuditEntry
        {
            Id = Guid.NewGuid(),
            Request = "{}",
            Result = """{"status":"success","response":"ok"}""",
            CompletedAt = DateTime.UtcNow
        };

        var info = PointControlAuditSerializer.ToDomain(entry);

        Assert.Equal(PointControlResult.Success, info.Result);
        Assert.Equal("""{"status":"success","response":"ok"}""", info.Response);
    }

    [Fact]
    public void ToDomain_ParsesFailedResult()
    {
        var entry = new PointControlAuditEntry
        {
            Id = Guid.NewGuid(),
            Request = "{}",
            Result = """{"status":"failed","response":"err"}"""
        };

        var info = PointControlAuditSerializer.ToDomain(entry);

        Assert.Equal(PointControlResult.Failed, info.Result);
    }

    [Fact]
    public void ToDomain_NullResult_GivesNullResult()
    {
        var entry = new PointControlAuditEntry { Id = Guid.NewGuid(), Request = "{}" };

        var info = PointControlAuditSerializer.ToDomain(entry);

        Assert.Null(info.Result);
        Assert.Null(info.Response);
    }

    // ─── PointControlAuditSerializer: ReadStatus (#162) ────────────────────

    [Fact]
    public void ReadStatus_NullResult_IsPending()
    {
        Assert.Equal("pending", PointControlAuditSerializer.ReadStatus(null));
    }

    [Fact]
    public void ReadStatus_ParsesSuccessAndFailed()
    {
        Assert.Equal("success", PointControlAuditSerializer.ReadStatus("""{"status":"success","response":"{}"}"""));
        Assert.Equal("failed", PointControlAuditSerializer.ReadStatus("""{"status":"failed","response":"err"}"""));
    }

    [Fact]
    public void ReadStatus_MalformedOrMissingStatus_IsPending()
    {
        Assert.Equal("pending", PointControlAuditSerializer.ReadStatus("not json"));
        Assert.Equal("pending", PointControlAuditSerializer.ReadStatus("""{"response":"{}"}"""));
    }

    // ─── Round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_CreateAndComplete_PreservesAllFields()
    {
        var id = Guid.NewGuid();
        var body = """{"type":"Hono","pointId":"pt-5","tenantId":"t1"}""";
        var original = new PointControlInfo
        {
            id = id,
            Type = "Hono",
            Body = body,
            PointId = "pt-5",
            Result = PointControlResult.Success,
            Response = """{"ack":true}"""
        };

        var entry = PointControlAuditSerializer.ToEntry(original);
        PointControlAuditSerializer.ApplyResult(entry, original);
        var restored = PointControlAuditSerializer.ToDomain(entry);

        Assert.Equal(id, restored.id);
        Assert.Equal(body, restored.Body);
        Assert.Equal("Hono", restored.Type);
        Assert.Equal(PointControlResult.Success, restored.Result);
    }
}
