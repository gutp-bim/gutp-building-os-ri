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
    /// <summary>The actor the ToEntry tests pass when the actor itself is not what is under test.</summary>
    private static readonly ControlActor SomeActor = ControlActor.From("kc-sub-1");

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
        Assert.Equal("", entry.ActorSub);
        Assert.Null(entry.ActorName);
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

        var entry = PointControlAuditSerializer.ToEntry(info, SomeActor);

        Assert.Equal(id, entry.Id);
        Assert.Equal("p-1", entry.PointId);
        Assert.Equal(body, entry.Request);
    }

    [Fact]
    public void ToEntry_ExtractsPointId_FromBodyWhenPropertyIsNull()
    {
        var body = """{"type":"Hono","pointId":"extracted-point"}""";
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Hono", Body = body, PointId = null };

        var entry = PointControlAuditSerializer.ToEntry(info, SomeActor);

        Assert.Equal("extracted-point", entry.PointId);
    }

    [Fact]
    public void ToEntry_PointIdFallsBackToEmptyString_WhenAbsent()
    {
        // #235 review: back-compat with the prior Npgsql writer — no point id ⇒ "" (not null),
        // so existing point_id-based queries / aggregates / index selectivity are unchanged.
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Kandt", Body = "{}", PointId = null };

        var entry = PointControlAuditSerializer.ToEntry(info, SomeActor);

        Assert.Equal(string.Empty, entry.PointId);
    }

    [Fact]
    public void ToEntry_ResultIsNull_BeforeUpdate()
    {
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Kandt", Body = "{}" };

        var entry = PointControlAuditSerializer.ToEntry(info, SomeActor);

        Assert.Null(entry.Result);
        Assert.Null(entry.CompletedAt);
    }

    // ─── PointControlAuditSerializer: ToEntry の actor (#461) ──────────────

    [Fact]
    public void ToEntry_RecordsWhoIssuedTheControl()
    {
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Kandt", Body = "{}", PointId = "p-1" };

        var entry = PointControlAuditSerializer.ToEntry(info, ControlActor.From("kc-sub-9", "Yamada"));

        Assert.Equal("kc-sub-9", entry.ActorSub);
        Assert.Equal("Yamada", entry.ActorName);
    }

    [Fact]
    public void ToEntry_ActorNameIsOptional()
    {
        // admin_audit's actor_name is nullable and every existing writer passes null; the control
        // audit keeps the same shape rather than inventing a placeholder display name.
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Kandt", Body = "{}" };

        var entry = PointControlAuditSerializer.ToEntry(info, ControlActor.From("kc-sub-9"));

        Assert.Equal("kc-sub-9", entry.ActorSub);
        Assert.Null(entry.ActorName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToEntry_BlankActorSub_FallsBackToTheUnknownSentinel(string? sub)
    {
        // actor_sub is NOT NULL, and a blank principal is not an identity. "unknown" is the same
        // sentinel AuthorizationContextMiddleware already uses for an unresolvable principal, so a
        // row that cannot name its actor says so rather than carrying an empty string.
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "Kandt", Body = "{}" };

        var entry = PointControlAuditSerializer.ToEntry(info, ControlActor.From(sub));

        Assert.Equal(ControlActor.UnknownSub, entry.ActorSub);
    }

    [Fact]
    public void ControlActor_TrimsAndNormalizesBlankName()
    {
        var actor = ControlActor.From("  kc-sub-9  ", "   ");

        Assert.Equal("kc-sub-9", actor.Sub);
        Assert.Null(actor.Name);
    }

    [Fact]
    public void PointControlInfo_CarriesNoActor_SoTheIdentityIsNeverPublishedToGateways()
    {
        // PointControlInfo is JSON-serialized onto NATS by NatsPointControlCommandPublisher and
        // forwarded down the egress stream to the gateway. An actor field on it would hand the
        // operator's identity to every gateway, so the actor travels beside it, not inside it.
        var info = new PointControlInfo { id = Guid.NewGuid(), Type = "BacnetSim", Body = "{}", PointId = "p-1" };

        var wire = JsonSerializer.Serialize(info);

        using var doc = JsonDocument.Parse(wire);
        Assert.DoesNotContain(
            doc.RootElement.EnumerateObject(),
            p => p.Name.Contains("actor", StringComparison.OrdinalIgnoreCase));
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

        var entry = PointControlAuditSerializer.ToEntry(original, SomeActor);
        PointControlAuditSerializer.ApplyResult(entry, original);
        var restored = PointControlAuditSerializer.ToDomain(entry);

        Assert.Equal(id, restored.id);
        Assert.Equal(body, restored.Body);
        Assert.Equal("Hono", restored.Type);
        Assert.Equal(PointControlResult.Success, restored.Result);
    }
}
