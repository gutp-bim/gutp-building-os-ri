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
        INatsSub<byte[]>? resultSub = null;
        ILatestValueSubscription? values = null;
        try
        {
            var config = await configResolver.ResolveAsync(request.Point.Id, ct).ConfigureAwait(false);
            resultSub = await nats.SubscribeCoreAsync<byte[]>(EgressSubjects.Result(request.ControlId), cancellationToken: abandon.Token)
                .ConfigureAwait(false);
            await nats.PingAsync(ct).ConfigureAwait(false); // fence the SUB before the command is published
            // Every reading, not only the newest: a match followed by another value must not be lost.
            values = config.FeedbackPointId is { } fb ? feed.SubscribeEveryValue([fb]).Subscription : null;

            // The window and the stale-reading filter count from here — the command is published right after this
            // returns — not from before the config lookup and the fence.
            var sentAt = _clock.GetUtcNow();
            var run = new Run(request with { SentAt = sentAt }, config, resultSub, values, abandon);
            _ = Task.Run(() => RunAsync(run), CancellationToken.None);
            return new Handle(abandon);
        }
        catch (Exception ex)
        {
            // Nothing owns these yet: release them on every failed start, including a cancelled request.
            values?.Dispose();
            if (resultSub is not null)
            {
                try { await resultSub.UnsubscribeAsync().ConfigureAwait(false); } catch { /* already closed */ }
            }
            abandon.Cancel();
            abandon.Dispose();
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            logger.LogWarning(ex, "Control feedback could not start for {ControlId}; the control is not confirmed", request.ControlId);
            return Handle.Noop;
        }
    }

    private sealed record Run(
        ControlFeedbackRequest Request, ControlFeedbackConfig Config, INatsSub<byte[]> ResultSub,
        ILatestValueSubscription? Values, CancellationTokenSource Abandon);

    private async Task RunAsync(Run run)
    {
        var (request, config, resultSub, values, abandon) = run;
        var started = _clock.GetUtcNow();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
        try
        {
            // Correlate from the moment the command goes out, while the gateway's answer is still on its way: the
            // feed keeps only the newest unread value per point, so a matching reading followed by another one would
            // otherwise be lost before we started looking.
            var (correlation, unavailable) = Prepare(request, config, values);
            var observing = correlation is null
                ? Task.CompletedTask
                : ObserveAsync(values!, correlation, request.SentAt + config.Window, window.Token);

            if (!await WaitForSuccessAsync(resultSub, abandon.Token).ConfigureAwait(false))
            {
                window.Cancel(); // failed / abandoned: nothing to confirm
                await observing.ConfigureAwait(false);
                return;
            }

            await audit.RecordFeedbackAsync(request.ControlId, ControlFeedbackStatuses.Pending, null, null).ConfigureAwait(false);
            await observing.ConfigureAwait(false);
            var outcome = correlation is null ? unavailable! : correlation.Result ?? correlation.Finish();
            // The feedback value is another point's telemetry unless the point confirms itself. Whoever can read
            // this control's history (a read of the controlled point) need not be allowed to read that point, so
            // only the outcome is recorded and sent for a separate feedback point — never its value.
            if (!string.Equals(config.FeedbackPointId, request.Point.Id, StringComparison.Ordinal))
                outcome = outcome with { ActualValue = null };
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

    /// <summary>The correlation to run, or — when the control cannot be confirmed — the unavailable result.</summary>
    private static (ControlFeedbackCorrelation? Correlation, ControlFeedbackResult? Unavailable) Prepare(
        ControlFeedbackRequest request, ControlFeedbackConfig config, ILatestValueSubscription? values)
    {
        if (values is null || config.FeedbackPointId is null)
            return (null, new(ControlFeedbackOutcome.Unavailable, null, ControlFeedbackUnavailableReasons.NoFeedbackPoint));
        if (request.Point.IsReleasePoint)
            return (null, new(ControlFeedbackOutcome.Unavailable, null, ControlFeedbackUnavailableReasons.ReleasePoint));
        var expected = ControlExpectedValueResolver.Resolve(
            request.Schema, request.RequestedValue, config.Tolerance, request.Point.IsReleasePoint);
        return expected is null
            ? (null, new(ControlFeedbackOutcome.Unavailable, null, ControlFeedbackUnavailableReasons.NoExpectedValue))
            : (new ControlFeedbackCorrelation(expected, request.SentAt), null);
    }

    /// <summary>Feeds the correlation until it matches or the window (counted from the command) ends.</summary>
    private async Task ObserveAsync(
        ILatestValueSubscription values, ControlFeedbackCorrelation correlation, DateTimeOffset windowEnd, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = windowEnd - _clock.GetUtcNow();
        window.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            await foreach (var v in values.ReadAllAsync(window.Token).ConfigureAwait(false))
            {
                if (correlation.Observe(ToReading(v)) is not null) return;
            }
        }
        catch (OperationCanceledException)
        {
            // The window ended (or the control failed / was abandoned).
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
