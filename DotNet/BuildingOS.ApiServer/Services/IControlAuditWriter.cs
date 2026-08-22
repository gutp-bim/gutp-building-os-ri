using BuildingOS.Shared.Domain.PointControl;
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
    /// <summary>Opens the audit row for a control command that is about to be dispatched.</summary>
    Task RecordRequestAsync(PointControlInfo info, CancellationToken ct = default);

    /// <summary>Closes the audit row with the outcome reported for <paramref name="controlId"/>.</summary>
    Task RecordResultAsync(string controlId, bool success, string? response, CancellationToken ct = default);
}

public sealed class ControlAuditWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<ControlAuditWriter> logger) : IControlAuditWriter
{
    /// <summary>
    /// Upper bound on a single audit write. Long enough to absorb ordinary contention, short enough
    /// that a hung database degrades the audit trail instead of the control path.
    /// </summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    public Task RecordRequestAsync(PointControlInfo info, CancellationToken ct = default)
        => WriteAsync(
            info.id.ToString(),
            (repository, token) => repository.CreatePointControlInfoAsync(info, token),
            ct);

    public Task RecordResultAsync(string controlId, bool success, string? response, CancellationToken ct = default)
    {
        if (!Guid.TryParse(controlId, out var id))
        {
            logger.LogWarning("Control result for non-GUID controlId {ControlId} was not audited", controlId);
            return Task.CompletedTask;
        }

        // Update-by-id: the row is opened by RecordRequestAsync before the command is published, and
        // EfPointControlRepository only applies the result fields, so the request payload is kept.
        return WriteAsync(
            controlId,
            (repository, token) => repository.UpdatePointControlInfoAsync(new PointControlInfo
            {
                id = id,
                Result = success ? PointControlResult.Success : PointControlResult.Failed,
                Response = response,
            }, token),
            ct);
    }

    private async Task WriteAsync(
        string controlId,
        Func<IPointControlRepository, CancellationToken, Task> write,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeout);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPointControlRepository>();
            await write(repository, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogError(
                "Control audit write for {ControlId} timed out after {Seconds}s", controlId, WriteTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write the control audit trail for {ControlId}", controlId);
        }
    }
}
