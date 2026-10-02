using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Infrastructure.HealthEvents;
using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOs.ApiServer.Health;

/// <summary>What one evaluation cycle did (for the log and the tests).</summary>
public sealed record HealthEvaluationResult(bool Skipped, int Raised, int AlreadyOpen, int Updated, int Cleared)
{
    public static readonly HealthEvaluationResult SkippedIndexNotReady = new(true, 0, 0, 0, 0);
}

/// <summary>
/// One scan of the evaluator (#455): classified ledger → <see cref="HealthEventReconciler"/> → store. Kept apart
/// from the hosted service so the whole decision can be driven in a test with a snapshot and a store.
/// </summary>
public sealed class HealthEvaluationCycle(HealthEventReconciler reconciler)
{
    public async Task<HealthEvaluationResult> RunAsync(
        PointHealthSnapshot snapshot, IHealthEventStore store, DateTime now, ILogger logger, CancellationToken ct)
    {
        // A last-seen index that is still warming (or has lost its watch) cannot tell "never arrived" from "not
        // read yet". Judging on it would raise missing for points that are fine and — worse — read Unknown as
        // "the condition went away" and clear real events. Skip the whole scan instead.
        if (snapshot.IndexState != PointLastSeenIndexState.Ready)
        {
            logger.LogDebug("Health evaluation skipped: last-seen index is {State}", snapshot.IndexState);
            return HealthEvaluationResult.SkippedIndexNotReady;
        }

        var open = await store.ListOpenAsync(ct).ConfigureAwait(false);
        var plan = reconciler.Reconcile(snapshot.Items, open);

        var raised = 0;
        var already = 0;
        foreach (var c in plan.Raise)
        {
            if (await store.RaiseAsync(c, now, ct).ConfigureAwait(false) == RaiseOutcome.Raised)
            {
                raised++;
                Count("raised", c.Kind);
                logger.LogInformation("Health event raised: {Kind} {SubjectType} {Severity}", c.Kind, c.SubjectType, c.Severity);
            }
            else already++; // another replica got there first — converged on its row
        }

        foreach (var u in plan.Update)
        {
            await store.UpdateOpenAsync(u.Key.SubjectType, u.Key.SubjectId, u.Key.Kind, u.Severity, u.DetailJson, ct).ConfigureAwait(false);
            Count("updated", u.Key.Kind);
        }

        var cleared = await store.ClearAsync(plan.Clear.ToArray(), now, ct).ConfigureAwait(false);
        if (cleared > 0)
        {
            var kindById = open.ToDictionary(e => e.Id, e => e.Kind);
            foreach (var id in plan.Clear) Count("cleared", kindById.GetValueOrDefault(id, "unknown"));
            logger.LogInformation("Health events cleared: {Count}", cleared);
        }

        return new HealthEvaluationResult(false, raised, already, plan.Update.Count, cleared);
    }

    private static void Count(string action, string kind) =>
        BuildingOsMetrics.HealthEvents.Add(1, new KeyValuePair<string, object?>("action", action), new KeyValuePair<string, object?>("kind", kind));
}
