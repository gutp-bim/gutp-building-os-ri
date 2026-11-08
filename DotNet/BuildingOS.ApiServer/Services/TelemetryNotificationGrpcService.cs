using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Protos;
using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Grpc.Core;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// Pushes points' latest values to the browser (#636) from the API server's single <c>telemetry-latest</c>
/// watch (<see cref="LatestValueFeed"/>). Authorized as a point read on every requested point: one point the
/// caller cannot read refuses the whole call, without saying which (an unreadable point and a missing one look
/// the same, as in <see cref="IAuthorizedTwinView"/>).
///
/// <para><b>Authorization is decided when the stream opens, so a stream does not live forever.</b> It ends cleanly
/// after <see cref="MaxLifetime"/> (the 5-minute authorization-context cache every REST read already lives with),
/// or at the access token's <c>exp</c> if that comes first; the client then reconnects, which re-authenticates and
/// re-authorizes. A revoked permission or an expired token therefore stops the values within that bound.</para>
/// </summary>
public sealed class TelemetryNotificationGrpcService(
    LatestValueFeed feed,
    IAuthorizedTwinView twinView,
    ILogger<TelemetryNotificationGrpcService> logger,
    TimeProvider? clock = null) : TelemetryNotificationService.TelemetryNotificationServiceBase
{
    /// <summary>Most points one stream may watch, so one client cannot hold the feed for a whole building.</summary>
    public const int MaxPoints = 100;

    /// <summary>Longest a stream runs before the client must reconnect (and be authorized again).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>How long this stream may run: <see cref="MaxLifetime"/>, cut short by the token's <c>exp</c>.</summary>
    internal static TimeSpan Lifetime(System.Security.Claims.ClaimsPrincipal user, DateTimeOffset now)
    {
        var exp = user.FindFirst("exp")?.Value;
        if (long.TryParse(exp, out var seconds))
        {
            var untilExpiry = DateTimeOffset.FromUnixTimeSeconds(seconds) - now;
            if (untilExpiry < MaxLifetime) return untilExpiry < TimeSpan.Zero ? TimeSpan.Zero : untilExpiry;
        }
        return MaxLifetime;
    }

    public override async Task WatchLatest(
        WatchLatestRequest request, IServerStreamWriter<LatestValueEvent> responseStream, ServerCallContext context)
    {
        var http = context.GetHttpContext();
        if (http.User.Identity?.IsAuthenticated != true)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication required"));
        var auth = http.GetAuthorizationContext();

        var pointIds = request.PointIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (pointIds.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "point_ids must name at least one point"));
        if (pointIds.Length > MaxPoints)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"point_ids may name at most {MaxPoints} points"));

        var lifetime = Lifetime(http.User, _clock.GetUtcNow());
        if (lifetime <= TimeSpan.Zero)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Access token expired"));

        var ct = context.CancellationToken;
        foreach (var id in pointIds)
        {
            var read = await twinView.GetPointAsync(auth, id, ct).ConfigureAwait(false);
            if (read is not TwinGetResult<Point>.Ok)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Not allowed to watch the requested points"));
        }

        var (subscription, snapshot) = feed.Subscribe(pointIds);
        using var _ = subscription;
        logger.LogDebug("WatchLatest opened for {PointCount} point(s)", pointIds.Length);

        // The last reading sent per point (time *and* value): the watch can re-deliver an unchanged value (a reconnect
        // replays the bucket), which is not a change — but a different value with the same timestamp is one (the hot
        // store keeps the later arrival for an equal timestamp, #625).
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        expiry.CancelAfter(lifetime);
        try
        {
            // Every write is bounded by the lifetime too, so a slow client cannot hold the stream past it.
            foreach (var v in snapshot)
            {
                expiry.Token.ThrowIfCancellationRequested();
                var e = ToEvent(v, snapshot: true);
                await responseStream.WriteAsync(e, expiry.Token).ConfigureAwait(false);
                sent[v.PointId] = ReadingKey(e);
                Count("snapshot");
            }

            await foreach (var v in subscription.ReadAllAsync(expiry.Token).ConfigureAwait(false))
            {
                var e = ToEvent(v, snapshot: false);
                var key = ReadingKey(e);
                if (sent.TryGetValue(v.PointId, out var last) && string.Equals(last, key, StringComparison.Ordinal))
                    continue;
                await responseStream.WriteAsync(e, expiry.Token).ConfigureAwait(false);
                sent[v.PointId] = key;
                Count("change");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The browser closed the stream (navigated away, tab closed): the normal end.
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
            // The lifetime is up: end cleanly so the client reconnects and is authorized afresh.
            logger.LogDebug("WatchLatest reached its lifetime of {Lifetime}; client will reconnect", lifetime);
        }
    }

    internal static LatestValueEvent ToEvent(LatestValue v, bool snapshot)
    {
        var e = new LatestValueEvent { PointId = v.PointId, Datetime = v.Datetime ?? "", Snapshot = snapshot };
        // ADR-0006: the discriminator decides which half is the reading; a numeric value left beside a
        // string/boolean reading is not the value.
        switch (v.ValueType?.ToLowerInvariant())
        {
            case "string" when v.ValueText is not null:
                e.StringValue = v.ValueText;
                break;
            case "boolean" when v.ValueBool is { } b:
                e.BoolValue = b;
                break;
            case null or "" or "number" when v.Value is { } n && double.IsFinite(n):
                e.NumberValue = n;
                break;
        }
        return e;
    }

    /// <summary>Identity of a reading as the browser sees it: its timestamp and its value.</summary>
    internal static string ReadingKey(LatestValueEvent e) => e.ValueCase switch
    {
        LatestValueEvent.ValueOneofCase.NumberValue => $"{e.Datetime}\u0001n\u0001{e.NumberValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
        LatestValueEvent.ValueOneofCase.StringValue => $"{e.Datetime}\u0001s\u0001{e.StringValue}",
        LatestValueEvent.ValueOneofCase.BoolValue => $"{e.Datetime}\u0001b\u0001{e.BoolValue}",
        _ => $"{e.Datetime}\u0001-",
    };

    private static void Count(string kind) =>
        BuildingOsMetrics.TelemetryFeedEventsSent.Add(1, new KeyValuePair<string, object?>("kind", kind));
}
