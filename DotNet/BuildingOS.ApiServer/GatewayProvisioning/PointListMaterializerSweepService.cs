using System.Threading.Channels;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>
/// Non-blocking "the twin changed, re-materialize soon" signal (point-list-projection plan, Phase B).
/// A capacity-1 channel with <see cref="BoundedChannelFullMode.DropWrite"/> naturally coalesces bursts:
/// several <see cref="RequestSweep"/> calls in quick succession (e.g. a run of admin imports) collapse
/// into the single pending sweep the background loop is already about to run — a caller never blocks
/// on, or waits for, the actual rebuild.
/// </summary>
public interface IPointListMaterializerSweepTrigger
{
    void RequestSweep();
}

/// <summary>
/// Hosted background loop that runs <see cref="IPointListMaterializer.RebuildAllAsync"/> whenever
/// triggered, plus a coarse safety-net interval so any write path this plan didn't enumerate (or a
/// dropped trigger) still self-heals eventually. <see cref="IPointListMaterializer"/> is scoped (it
/// depends on the scoped <see cref="BuildingOS.Shared.Infrastructure.IDigitalTwinDatabase"/> and the
/// EF Core-backed cache store), so this singleton-lifetime service resolves it through a fresh scope
/// per sweep rather than holding one — the same pattern as <c>ControlAuditWriter</c>.
/// </summary>
public sealed class PointListMaterializerSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<PointListMaterializerSweepService> logger)
    : BackgroundService, IPointListMaterializerSweepTrigger
{
    /// <summary>Worst-case staleness when nothing explicitly triggers a sweep. Acceptable because the
    /// read path (GatewayProvisioningController) always re-validates the cached ETag against the
    /// existing NATS-KV revision coordinator before trusting a cache hit — this interval only bounds
    /// how long the cache stays cold/stale, never correctness.</summary>
    private static readonly TimeSpan SafetyNetInterval = TimeSpan.FromMinutes(20);

    private readonly Channel<bool> _requested = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void RequestSweep() => _requested.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var safetyNetTimeout = new CancellationTokenSource(SafetyNetInterval);
            using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, safetyNetTimeout.Token);
            try
            {
                await _requested.Reader.ReadAsync(waitToken.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (OperationCanceledException)
            {
                // Safety-net interval elapsed with no explicit trigger — sweep anyway.
            }

            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var materializer = scope.ServiceProvider.GetRequiredService<IPointListMaterializer>();
            await materializer.RebuildAllAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Point-list materializer sweep failed; the cache stays as-is until the next trigger or safety-net interval");
        }
    }
}
