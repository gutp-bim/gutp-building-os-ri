using System.Globalization;
using System.Text.Json;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.ControlFeedback;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using NATS.Client.Core;

namespace BuildingOs.ApiServer.Services;

/// <summary>What is being controlled, as the confirmation needs it (#637).</summary>
public sealed record ControlFeedbackRequest(
    string ControlId, Point Point, ControlSchema? Schema, double RequestedValue, DateTimeOffset SentAt);

/// <summary>A confirmation that has started listening. Abandon it when the command turned out not to be sent.</summary>
public interface IControlFeedbackHandle
{
    void Abandon();
}

public interface IControlFeedbackCoordinator
{
    /// <summary>
    /// Starts confirming a control. Call it <b>before</b> publishing the command: it subscribes to the gateway's
    /// answer and to the feedback point's values first, so neither can be missed. Never throws for a problem of
    /// its own — confirmation is an aid, and must not fail a control.
    /// </summary>
    Task<IControlFeedbackHandle> BeginAsync(ControlFeedbackRequest request, CancellationToken ct);
}

/// <summary>
/// Confirms controls (#637): after the gateway reports SUCCESS, watches the twin-declared feedback point
/// (<c>bos:feedbackPoint</c>) for the confirmation window and decides matched / mismatch / timeout / unavailable
/// (<see cref="ControlFeedbackCorrelation"/>). The outcome goes to the audit row and to
/// <c>building-os.control.feedback.{controlId}</c>, from which any replica's WaitForResult forwards it.
///
/// <para>Runs in the API replica that accepted the control, in the background — it does not depend on the browser
/// still being there. A process that stops mid-confirmation leaves the audit row <c>pending</c>;
/// <see cref="ControlFeedbackSweeper"/> closes such rows as unavailable / interrupted.</para>
/// </summary>
public sealed class ControlFeedbackCoordinator(
    INatsConnection nats,
    LatestValueFeed feed,
    IControlFeedbackConfigResolver configResolver,
    IControlAuditWriter audit,
    ILogger<ControlFeedbackCoordinator> logger,
    TimeProvider? clock = null) : IControlFeedbackCoordinator
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>How long to wait for the gateway's answer before giving up on confirming.</summary>
    internal static readonly TimeSpan ResultWait = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IControlFeedbackHandle> BeginAsync(ControlFeedbackRequest request, CancellationToken ct)
    {
        var abandon = new CancellationTokenSource();
        try
        {
            var config = await configResolver.ResolveAsync(request.Point.Id, ct).ConfigureAwait(false);
            var result = await nats.SubscribeCoreAsync<byte[]>(EgressSubjects.Result(request.ControlId), cancellationToken: abandon.Token)
                .ConfigureAwait(false);
            await nats.PingAsync(ct).ConfigureAwait(false); // fence the SUB before the command is published
            var values = config.FeedbackPointId is { } fb ? feed.Subscribe([fb]).Subscription : null;

            _ = Task.Run(() => RunAsync(request, config, result, values, abandon), CancellationToken.None);
            return new Handle(abandon);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Control feedback could not start for {ControlId}; the control is not confirmed", request.ControlId);
            abandon.Dispose();
            return Handle.Noop;
        }
    }

    private async Task RunAsync(
        ControlFeedbackRequest request, ControlFeedbackConfig config, INatsSub<byte[]> resultSub,
        ILatestValueSubscription? values, CancellationTokenSource abandon)
    {
        var started = _clock.GetUtcNow();
        try
        {
            if (!await WaitForSuccessAsync(resultSub, abandon.Token).ConfigureAwait(false)) return; // failed / abandoned: nothing to confirm

            await audit.RecordFeedbackAsync(request.ControlId, ControlFeedbackStatuses.Pending, null, null).ConfigureAwait(false);
            var outcome = await DecideAsync(request, config, values, abandon.Token).ConfigureAwait(false);
            await FinishAsync(request.ControlId, outcome, started).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested)
        {
            // Abandoned: the command was not sent after all.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Control feedback failed for {ControlId}", request.ControlId);
            await FinishAsync(request.ControlId,
                new ControlFeedbackResult(ControlFeedbackOutcome.Unavailable, null, ControlFeedbackUnavailableReasons.Interrupted),
                started).ConfigureAwait(false);
        }
        finally
        {
            values?.Dispose();
            try { await resultSub.UnsubscribeAsync().ConfigureAwait(false); } catch { /* already closed */ }
            abandon.Dispose();
        }
    }

    private async Task<bool> WaitForSuccessAsync(INatsSub<byte[]> resultSub, CancellationToken abandon)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(abandon);
        wait.CancelAfter(ResultWait);
        try
        {
            await foreach (var msg in resultSub.Msgs.ReadAllAsync(wait.Token).ConfigureAwait(false))
            {
                if (msg.Data is null) continue;
                var dto = JsonSerializer.Deserialize<ResultDto>(msg.Data, Json);
                if (dto is not null) return dto.Success;
            }
        }
        catch (OperationCanceledException) when (!abandon.IsCancellationRequested)
        {
            // No answer from the gateway: the control's own timeout reports that; there is nothing to confirm.
        }
        return false;
    }

    private async Task<ControlFeedbackResult> DecideAsync(
        ControlFeedbackRequest request, ControlFeedbackConfig config, ILatestValueSubscription? values, CancellationToken abandon)
    {
        if (values is null || config.FeedbackPointId is null)
            return new(ControlFeedbackOutcome.Unavailable, null, ControlFeedbackUnavailableReasons.NoFeedbackPoint);
        if (request.Point.IsReleasePoint)
            return new(ControlFeedbackOutcome.Unavailable, null, ControlFeedbackUnavailableReasons.ReleasePoint);
        var expected = ControlExpectedValueResolver.Resolve(
            request.Schema, request.RequestedValue, config.Tolerance, request.Point.IsReleasePoint);
        if (expected is null)
            return new(ControlFeedbackOutcome.Unavailable, null, ControlFeedbackUnavailableReasons.NoExpectedValue);

        var correlation = new ControlFeedbackCorrelation(expected, request.SentAt);
        var remaining = request.SentAt + config.Window - _clock.GetUtcNow();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(abandon);
        window.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            await foreach (var v in values.ReadAllAsync(window.Token).ConfigureAwait(false))
            {
                if (correlation.Observe(ToReading(v)) is { } decided) return decided;
            }
        }
        catch (OperationCanceledException) when (!abandon.IsCancellationRequested)
        {
            // The window ended.
        }
        return correlation.Finish();
    }

    /// <summary>The feedback point's reading as a number: ADR-0006's discriminator decides which half is meant.</summary>
    internal static FeedbackReading ToReading(LatestValue v)
    {
        DateTimeOffset? at = DateTimeOffset.TryParse(v.Datetime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        double? value = (v.ValueType ?? "number").Trim().ToLowerInvariant() switch
        {
            "boolean" => v.ValueBool is { } b ? (b ? 1 : 0) : null,
            "string" => double.TryParse(v.ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => v.Value,
        };
        return new FeedbackReading(value, at);
    }

    private async Task FinishAsync(string controlId, ControlFeedbackResult outcome, DateTimeOffset started)
    {
        var status = ControlFeedbackStatuses.Of(outcome.Outcome);
        var value = outcome.ActualValue is { } a ? ControlExpectedValueResolver.Format(a) : outcome.Reason;
        await audit.RecordFeedbackAsync(controlId, status, value, _clock.GetUtcNow().UtcDateTime).ConfigureAwait(false);
        try
        {
            await nats.PublishAsync(EgressSubjects.Feedback(controlId),
                JsonSerializer.SerializeToUtf8Bytes(new ControlFeedbackMessage(status, value))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Control feedback for {ControlId} could not be published (audited)", controlId);
        }
        BuildingOsMetrics.ControlFeedback.Add(1, new KeyValuePair<string, object?>("result", status));
        BuildingOsMetrics.ControlFeedbackDuration.Record((_clock.GetUtcNow() - started).TotalSeconds,
            new KeyValuePair<string, object?>("result", status));
        logger.LogInformation("Control {ControlId} feedback: {Status} ({Value})", controlId, status, value);
    }

    private sealed record ResultDto(bool Success, string? Response);

    private sealed class Handle(CancellationTokenSource? abandon) : IControlFeedbackHandle
    {
        public static readonly Handle Noop = new(null);

        public void Abandon()
        {
            try { abandon?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        }
    }
}

/// <summary>
/// #637: closes confirmations a stopped process left <c>pending</c> (restart / crash mid-window) as unavailable /
/// interrupted, once they are older than any window could be. Runs at start and then every few minutes, on every
/// replica — the update is conditional on <c>pending</c>, so replicas converge.
/// </summary>
public sealed class ControlFeedbackSweeper(IServiceScopeFactory scopes, ILogger<ControlFeedbackSweeper> logger, TimeProvider? clock = null)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan Grace = ControlFeedbackConfig.MaxWindow + ControlFeedbackCoordinator.ResultWait + TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var repository = scope.ServiceProvider.GetService<IPointControlRepository>();
                if (repository is not null)
                {
                    var now = _clock.GetUtcNow().UtcDateTime;
                    var closed = await repository.CloseInterruptedFeedbackAsync(now - Grace, now, stoppingToken).ConfigureAwait(false);
                    if (closed > 0) logger.LogInformation("Closed {Count} interrupted control confirmation(s)", closed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Control feedback sweep failed (retrying)");
            }
            try { await Task.Delay(Interval, _clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
