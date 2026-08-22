using BuildingOs.ApiServer.Services;
using BuildingOS.Shared.Domain.PointControl;
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
        repo.Setup(r => r.CreatePointControlInfoAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PointControlInfo, CancellationToken>((i, _) => persisted = i)
            .Returns(Task.CompletedTask);

        await writer.RecordRequestAsync(info, CancellationToken.None);

        Assert.Same(info, persisted);
    }

    [Fact]
    public async Task RecordRequestAsync_Swallows_PersistenceFailure()
    {
        var (writer, repo) = Build();
        repo.Setup(r => r.CreatePointControlInfoAsync(It.IsAny<PointControlInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit store is down"));

        // Availability over auditability: this runs on the control hot path.
        await writer.RecordRequestAsync(
            new PointControlInfo { id = Guid.NewGuid(), Type = "BacnetSim", Body = "{}" }, CancellationToken.None);
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
}
