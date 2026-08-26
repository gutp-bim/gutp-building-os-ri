using System.Text;
using System.Text.Json;
using NATS.Client.Core;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// Persists every control outcome to the audit trail (#333).
///
/// This deliberately does <b>not</b> reuse <see cref="NatsControlResultBus"/>'s per-command
/// subscription: that one exists only while a caller is waiting, and
/// <c>PointControlGrpcService.WaitForResult</c> tears it down in its <c>finally</c> — on timeout
/// (<c>CONTROL_RESULT_TIMEOUT_SEC</c>, 10s by default) or when the browser navigates away. A gateway
/// that answers a second later would then leave the audit row "pending" forever, which is the same
/// hole the gateway-offline path was written to avoid. Keeping the audit on its own long-lived
/// subscription also keeps a slow database off the gRPC stream's teardown path.
///
/// Core NATS (not JetStream), matching the publishers: an outcome that arrives while the API server
/// is down is not replayed. The row stays "pending" — visibly incomplete rather than silently wrong.
/// </summary>
public sealed class ControlAuditResultSubscriber(
    INatsConnection nats,
    IControlAuditWriter auditWriter,
    ILogger<ControlAuditResultSubscriber> logger) : BackgroundService
{
    private const string ResultSubjectPrefix = "building-os.control.result.";
    private const string ResultSubjectWildcard = ResultSubjectPrefix + "*";

    /// <summary>
    /// One replica persists each result. Without this every replica would write the same row, at N×
    /// the database cost and with a nondeterministic completed_at.
    /// </summary>
    private const string QueueGroup = "control-audit";

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resubscribe rather than exit: a BackgroundService that returns stays dead for the life of
        // the process, and the host keeps serving traffic — so a single transient NATS error would
        // silently disable the audit trail until someone noticed and restarted the pod.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Control audit subscriber listening on {Subject}", ResultSubjectWildcard);

                await foreach (var msg in nats
                    .SubscribeAsync<byte[]>(ResultSubjectWildcard, QueueGroup, cancellationToken: stoppingToken)
                    .ConfigureAwait(false))
                {
                    await HandleAsync(msg, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // Normal shutdown.
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex, "Control audit subscription failed; retrying in {Seconds}s", ReconnectDelay.TotalSeconds);
            }

            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleAsync(NatsMsg<byte[]> msg, CancellationToken ct)
    {
        var controlId = msg.Subject.Length > ResultSubjectPrefix.Length
            ? msg.Subject[ResultSubjectPrefix.Length..]
            : null;
        if (string.IsNullOrEmpty(controlId) || msg.Data is null) return;

        ControlResultDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ControlResultDto>(Encoding.UTF8.GetString(msg.Data), JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Malformed control result on {Subject} was not audited", msg.Subject);
            return;
        }

        if (dto is null) return;
        await auditWriter.RecordResultAsync(controlId, dto.Success, dto.Response, ct).ConfigureAwait(false);
    }

    private record ControlResultDto(bool Success, string? Response);
}
