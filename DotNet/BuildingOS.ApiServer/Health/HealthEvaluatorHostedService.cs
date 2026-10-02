using System.Globalization;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.UserManagement;
using BuildingOS.Shared.Infrastructure.HealthEvents;

namespace BuildingOs.ApiServer.Health;

public sealed record HealthEvaluatorOptions(int IntervalSeconds, int RaiseAfterScans, int ClearAfterScans);

/// <summary>
/// The resident health evaluator (#455, ADR-0005 Phase 2b): every interval it classifies the whole twin the way
/// the data-health API does and turns the result into persisted events (raise / clear / re-grade), then trims
/// cleared events past the retention.
///
/// <para>It runs inside the API Server because everything the classification reads — the last-seen index, the
/// thresholds, the twin inventory, the gateway state, the database — already lives there. No lock is taken: several
/// replicas each scan, and the partial unique index plus conditional updates make their writes converge.</para>
/// </summary>
public sealed class HealthEvaluatorHostedService(
    IServiceScopeFactory scopes,
    HealthEvaluationCycle cycle,
    HealthEvaluatorOptions options,
    TimeProvider clock,
    ILogger<HealthEvaluatorHostedService> logger) : BackgroundService
{
    // The evaluator reads the whole twin — no caller to authorize — so it acts as the system with admin reach.
    private static readonly AuthorizationContext SystemAuth = new()
    {
        UserId = "system:health-evaluator",
        Role = RoleCatalog.Admin,
        Permissions = [],
    };

    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Health evaluator started: every {Interval}s, raise after {Raise} / clear after {Clear} consecutive scans",
            options.IntervalSeconds, options.RaiseAfterScans, options.ClearAfterScans);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds), clock);
        do
        {
            try
            {
                await ScanAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad scan (database blip, twin timeout) must not end the loop — a BackgroundService that
                // returns stays dead until the process restarts, silently ending event generation.
                logger.LogWarning(ex, "Health evaluation scan failed; will retry next interval");
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    internal async Task ScanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<PointHealthLedger>();
        var store = scope.ServiceProvider.GetRequiredService<IHealthEventStore>();

        var snapshot = await ledger.BuildAsync(SystemAuth, buildingDtId: null, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var result = await cycle.RunAsync(snapshot, store, now, logger, ct).ConfigureAwait(false);

        if (!result.Skipped) await PruneAsync(scope.ServiceProvider, store, now, ct).ConfigureAwait(false);
    }

    private async Task PruneAsync(IServiceProvider sp, IHealthEventStore store, DateTime now, CancellationToken ct)
    {
        if (clock.GetUtcNow() - _lastPrune < PruneEvery) return;
        _lastPrune = clock.GetUtcNow();

        var settings = await sp.GetRequiredService<ISystemSettingsService>().GetSettingsAsync(ct).ConfigureAwait(false);
        var raw = settings.FirstOrDefault(s => s.Key == SettingsRegistry.HealthEventRetentionDaysKey)?.Value;
        // 0 or less (or unreadable) means "keep forever": never delete on a value we cannot trust.
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) || days <= 0)
            return;

        var removed = await store.PruneClearedAsync(now.AddDays(-days), ct).ConfigureAwait(false);
        if (removed > 0) logger.LogInformation("Pruned {Count} cleared health events older than {Days} days", removed, days);
    }
}
