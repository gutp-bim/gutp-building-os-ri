using BuildingOS.Shared.Domain.PointControl;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// Writes control outcomes to <c>point_control_audit</c> from the result bus (#333).
///
/// The result bus is a singleton holding long-lived NATS subscriptions, while
/// <see cref="IPointControlRepository"/> is scoped (it wraps a DbContext) — this abstraction owns
/// that lifetime bridge so the bus keeps a single narrow dependency it can be tested against.
/// </summary>
public interface IControlAuditWriter
{
    /// <summary>
    /// Records the result of a dispatched control command. Best-effort: a persistence failure is
    /// logged but never propagated, so an audit outage cannot break result delivery to the caller.
    /// </summary>
    Task RecordResultAsync(string controlId, bool success, string? response, CancellationToken ct);
}

public sealed class ControlAuditWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<ControlAuditWriter> logger) : IControlAuditWriter
{
    public async Task RecordResultAsync(string controlId, bool success, string? response, CancellationToken ct)
    {
        if (!Guid.TryParse(controlId, out var id))
        {
            logger.LogWarning("Control result for non-GUID controlId {ControlId} was not audited", controlId);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPointControlRepository>();

            // Update-by-id: the request row is inserted by PointController before publishing, so a
            // missing row here means the command never reached the audit trail (already logged there).
            await repository.UpdatePointControlInfoAsync(new PointControlInfo
            {
                id = id,
                Result = success ? PointControlResult.Success : PointControlResult.Failed,
                Response = response,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record control audit result for {ControlId}", controlId);
        }
    }
}
