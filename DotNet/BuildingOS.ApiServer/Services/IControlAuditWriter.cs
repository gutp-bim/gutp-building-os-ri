using BuildingOS.Shared.Domain.PointControl;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// Writes the control audit trail to <c>point_control_audit</c> (#333).
///
/// Owns two policies so every caller inherits them: writes are <b>best-effort</b> (a failure is
/// logged, never propagated — an audit outage must not fail a control) and <b>time-bounded</b> (a
/// hung database must not stall the control path either, which a try/catch alone does not give us).
///
/// Callers span lifetimes — a scoped controller and a singleton background subscriber — so this
/// resolves the scoped repository per call rather than holding one.
/// </summary>
public interface IControlAuditWriter
{
    /// <summary>
    /// Opens the audit row for a control command that is about to be dispatched. Runs on the control
    /// hot path, so it is bounded more tightly than the background writes.
    /// </summary>
    /// <param name="info">The command being dispatched.</param>
    /// <param name="actor">
    /// The authenticated principal issuing it (#461). Required, not defaulted: it is always available
    /// at the control entry point, and the audit row is the only place it is kept.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordRequestAsync(PointControlInfo info, ControlActor actor, CancellationToken ct = default);

    /// <summary>Closes the audit row with the outcome reported for <paramref name="controlId"/>.</summary>
    Task RecordResultAsync(string controlId, bool success, string? response, CancellationToken ct = default);

    /// <summary>
    /// Closes the audit row as failed, but only while it is still pending. Used by the dispatch-error
    /// paths, where the command may in fact have reached a gateway: if a real outcome has already been
    /// recorded, it is the truth and must not be overwritten with our local failure.
    /// </summary>
    Task RecordFailureIfPendingAsync(string controlId, string? response, CancellationToken ct = default);
}

public sealed class ControlAuditWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<ControlAuditWriter> logger) : IControlAuditWriter
{
    /// <summary>
    /// Upper bound on the pre-publish insert. Deliberately short: it is awaited before the command is
    /// sent, so every millisecond here is added to the operator's control round-trip (which the gRPC
    /// wait then bounds again via CONTROL_RESULT_TIMEOUT_SEC).
    /// </summary>
    private static readonly TimeSpan RequestWriteTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on the background result writes. Nothing is waiting on these, so they can be more
    /// patient than the hot path — but still bounded, to not pin a scope on a hung database.
    /// </summary>
    private static readonly TimeSpan ResultWriteTimeout = TimeSpan.FromSeconds(5);

    public Task RecordRequestAsync(PointControlInfo info, ControlActor actor, CancellationToken ct = default)
        => WriteAsync(
            info.id.ToString(),
            RequestWriteTimeout,
            (repository, token) => repository.CreatePointControlInfoAsync(info, actor, token),
            ct);

    public Task RecordResultAsync(string controlId, bool success, string? response, CancellationToken ct = default)
    {
        if (!TryParse(controlId, out var id)) return Task.CompletedTask;

        // Update-by-id: the row is opened by RecordRequestAsync before the command is published, and
        // EfPointControlRepository only applies the result fields, so the request payload is kept.
        return WriteAsync(
            controlId,
            ResultWriteTimeout,
            (repository, token) => repository.UpdatePointControlInfoAsync(Outcome(id, success, response), token),
            ct);
    }

    public Task RecordFailureIfPendingAsync(string controlId, string? response, CancellationToken ct = default)
    {
        if (!TryParse(controlId, out var id)) return Task.CompletedTask;

        return WriteAsync(controlId, ResultWriteTimeout, async (repository, token) =>
        {
            var existing = await repository.GetPointControlInfoAsync(id, token).ConfigureAwait(false);
            // Already resolved — a gateway did answer despite our local dispatch error. Leave it.
            if (existing?.Result is not null) return;

            await repository
                .UpdatePointControlInfoAsync(Outcome(id, success: false, response), token)
                .ConfigureAwait(false);
        }, ct);
    }

    private static PointControlInfo Outcome(Guid id, bool success, string? response) => new()
    {
        id = id,
        Result = success ? PointControlResult.Success : PointControlResult.Failed,
        Response = response,
    };

    private bool TryParse(string controlId, out Guid id)
    {
        if (Guid.TryParse(controlId, out id)) return true;
        logger.LogWarning("Control result for non-GUID controlId {ControlId} was not audited", controlId);
        return false;
    }

    private async Task WriteAsync(
        string controlId,
        TimeSpan budget,
        Func<IPointControlRepository, CancellationToken, Task> write,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPointControlRepository>();
            await write(repository, timeout.Token).ConfigureAwait(false);
            Count("ok");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogError(
                "Control audit write for {ControlId} timed out after {Seconds}s", controlId, budget.TotalSeconds);
            Count("error");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write the control audit trail for {ControlId}", controlId);
            Count("error");
        }
    }

    // #418: a persistent DB outage otherwise lets control writes keep returning 202 while zero audit
    // rows are ever written, observable only via log-grepping — this counter makes that visible.
    private static void Count(string result) =>
        BuildingOsMetrics.ControlAuditWrites.Add(1, new KeyValuePair<string, object?>("result", result));
}
