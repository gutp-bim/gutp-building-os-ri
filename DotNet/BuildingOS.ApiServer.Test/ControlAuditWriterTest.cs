using System.Diagnostics.Metrics;
using BuildingOs.ApiServer.Services;
using BuildingOS.Shared.Domain.PointControl;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// The result half of #333: outcomes arriving on building-os.control.result.{controlId} must land in
/// point_control_audit. This is the single point where both dispatch paths converge (in-process
/// handlers and real gateways via GatewayBridge), so it is the one place worth guarding.
/// </summary>
public class ControlAuditWriterTest
{
    private static (ControlAuditWriter writer, Mock<IPointControlRepository> repo) Build()
    {
        var repo = new Mock<IPointControlRepository>();
        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);

        var writer = new ControlAuditWriter(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ControlAuditWriter>.Instance);

        return (writer, repo);
    }

    [Fact]
    public async Task RecordRequestAsync_OpensTheAuditRow()
    {
        var (writer, repo) = Build();
        var info = new PointControlInfo { id = Guid.NewGuid(), PointId = "PT001", Type = "BacnetSim", Body = "{}" };
        PointControlInfo? persisted = null;
        repo.Setup(r => r.CreatePointControlInfoAsync(
                It.IsAny<PointControlInfo>(), It.IsAny<ControlActor>(), It.IsAny<CancellationToken>()))
            .Callback<PointControlInfo, ControlActor, CancellationToken>((i, _, _) => persisted = i)
            .Returns(Task.CompletedTask);

        await writer.RecordRequestAsync(info, ControlActor.From("kc-sub-1"), CancellationToken.None);

        Assert.Same(info, persisted);
    }

    [Fact]
    public async Task RecordRequestAsync_CarriesTheActorToThePersistedRow()
    {
        // #461: the authenticated principal exists at the control entry point; the audit row is
        // where it has to land, or the trail records what was done but not who did it.
        var (writer, repo) = Build();
        ControlActor? persisted = null;
        repo.Setup(r => r.CreatePointControlInfoAsync(
                It.IsAny<PointControlInfo>(), It.IsAny<ControlActor>(), It.IsAny<CancellationToken>()))
            .Callback<PointControlInfo, ControlActor, CancellationToken>((_, a, _) => persisted = a)
            .Returns(Task.CompletedTask);

        await writer.RecordRequestAsync(
            new PointControlInfo { id = Guid.NewGuid(), Type = "BacnetSim", Body = "{}" },
            ControlActor.From("kc-sub-7", "Yamada"),
            CancellationToken.None);

        Assert.Equal("kc-sub-7", persisted!.Sub);
        Assert.Equal("Yamada", persisted.Name);
    }

    [Fact]
    public async Task RecordRequestAsync_Swallows_PersistenceFailure()
    {
        var (writer, repo) = Build();
        repo.Setup(r => r.CreatePointControlInfoAsync(
                It.IsAny<PointControlInfo>(), It.IsAny<ControlActor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit store is down"));

        // Availability over auditability: this runs on the control hot path.
        await writer.RecordRequestAsync(
            new PointControlInfo { id = Guid.NewGuid(), Type = "BacnetSim", Body = "{}" },
            ControlActor.From("kc-sub-1"),
            CancellationToken.None);
    }

    [Theory]
    [InlineData(true, PointControlResult.Success)]
    [InlineData(false, PointControlResult.Failed)]
    public async Task RecordResultAsync_PersistsOutcome(bool success, PointControlResult expected)
    {
        var (writer, repo) = Build();
        var controlId = Guid.NewGuid();
        PointControlInfo? persisted = null;
        repo.Setup(r => r.UpdatePointControlInfoAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PointControlInfo, CancellationToken>((info, _) => persisted = info)
            .Returns(Task.CompletedTask);

        await writer.RecordResultAsync(controlId.ToString(), success, "{\"ok\":true}", CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(controlId, persisted!.id);
        Assert.Equal(expected, persisted.Result);
        Assert.Equal("{\"ok\":true}", persisted.Response);
    }

    [Fact]
    public async Task RecordFailureIfPendingAsync_ClosesAPendingRow()
    {
        var (writer, repo) = Build();
        var controlId = Guid.NewGuid();
        repo.Setup(r => r.GetPointControlInfoAsync(controlId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PointControlInfo { id = controlId, Type = "BacnetSim", Body = "{}" });
        PointControlInfo? persisted = null;
        repo.Setup(r => r.UpdatePointControlInfoAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PointControlInfo, CancellationToken>((info, _) => persisted = info)
            .Returns(Task.CompletedTask);

        await writer.RecordFailureIfPendingAsync(controlId.ToString(), "nats is down", CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(PointControlResult.Failed, persisted!.Result);
    }

    [Fact]
    public async Task RecordFailureIfPendingAsync_DoesNotOverwriteARealOutcome()
    {
        var (writer, repo) = Build();
        var controlId = Guid.NewGuid();
        // A dispatch error does not prove the command never reached a gateway; if the gateway did
        // answer, that outcome is the truth and our local failure must not clobber it.
        repo.Setup(r => r.GetPointControlInfoAsync(controlId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PointControlInfo
            {
                id = controlId, Type = "BacnetSim", Body = "{}", Result = PointControlResult.Success,
            });

        await writer.RecordFailureIfPendingAsync(controlId.ToString(), "nats is down", CancellationToken.None);

        repo.Verify(
            r => r.UpdatePointControlInfoAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RecordResultAsync_Ignores_NonGuidControlId()
    {
        var (writer, repo) = Build();

        await writer.RecordResultAsync("not-a-guid", true, null, CancellationToken.None);

        repo.Verify(r => r.UpdatePointControlInfoAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordResultAsync_Swallows_PersistenceFailure()
    {
        var (writer, repo) = Build();
        repo.Setup(r => r.UpdatePointControlInfoAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit store is down"));

        // Must not throw: the caller is the long-lived result subscriber, and one bad write must not
        // take the subscription down with it.
        await writer.RecordResultAsync(Guid.NewGuid().ToString(), true, null, CancellationToken.None);
    }

    // ── Write-failure observability (#418) ────────────────────────────────────
    // A persistent DB outage lets control writes keep swallowing the persistence exception as
    // log-only, so control requests keep returning 202 while zero audit rows are ever written —
    // invisible except via log-grepping. A dedicated result=ok/error counter makes that observable.

    private const string ControlAuditWritesInstrumentName = "building_os.control_audit.writes";

    [Fact]
    public async Task RecordRequestAsync_PersistenceFailure_IncrementsErrorMetric()
    {
        var (writer, repo) = Build();
        repo.Setup(r => r.CreatePointControlInfoAsync(
                It.IsAny<PointControlInfo>(), It.IsAny<ControlActor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit store is down"));

        var results = await RunCapturingWriteResultsAsync(() =>
            writer.RecordRequestAsync(
                new PointControlInfo { id = Guid.NewGuid(), Type = "BacnetSim", Body = "{}" },
                ControlActor.From("kc-sub-1"),
                CancellationToken.None));

        Assert.Contains("error", results);
        Assert.DoesNotContain("ok", results);
    }

    [Fact]
    public async Task RecordRequestAsync_Success_IncrementsOkMetric()
    {
        var (writer, repo) = Build();
        repo.Setup(r => r.CreatePointControlInfoAsync(
                It.IsAny<PointControlInfo>(), It.IsAny<ControlActor>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var results = await RunCapturingWriteResultsAsync(() =>
            writer.RecordRequestAsync(
                new PointControlInfo { id = Guid.NewGuid(), Type = "BacnetSim", Body = "{}" },
                ControlActor.From("kc-sub-1"),
                CancellationToken.None));

        Assert.Contains("ok", results);
        Assert.DoesNotContain("error", results);
    }

    [Fact]
    public async Task RecordResultAsync_PersistenceFailure_IncrementsErrorMetric()
    {
        var (writer, repo) = Build();
        repo.Setup(r => r.UpdatePointControlInfoAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit store is down"));

        var results = await RunCapturingWriteResultsAsync(() =>
            writer.RecordResultAsync(Guid.NewGuid().ToString(), true, null, CancellationToken.None));

        Assert.Contains("error", results);
    }

    /// <summary>
    /// Runs a single audit write and collects the ControlAuditWrites counter's `result` tag values
    /// emitted while it does. Only this test class constructs a real ControlAuditWriter, so there is
    /// no cross-class contention on this instrument the way GatewayIngressServiceTest guards against
    /// with a per-case gateway tag.
    /// </summary>
    private static async Task<List<string>> RunCapturingWriteResultsAsync(Func<Task> run)
    {
        var results = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == ControlAuditWritesInstrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "result" && tag.Value is string result) results.Add(result);
            }
        });
        listener.Start();

        await run();
        return results;
    }
}
