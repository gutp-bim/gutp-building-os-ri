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

        // The last datetime sent per point: the watch can re-deliver an unchanged value (a reconnect replays the
        // bucket), and a value equal to the snapshot is not a change.
        var sent = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        expiry.CancelAfter(lifetime);
        try
        {
            foreach (var v in snapshot)
            {
                await responseStream.WriteAsync(ToEvent(v, snapshot: true), ct).ConfigureAwait(false);
                sent[v.PointId] = v.Datetime;
                Count("snapshot");
            }

            await foreach (var v in subscription.ReadAllAsync(expiry.Token).ConfigureAwait(false))
            {
                if (sent.TryGetValue(v.PointId, out var last) && string.Equals(last, v.Datetime, StringComparison.Ordinal))
                    continue;
                await responseStream.WriteAsync(ToEvent(v, snapshot: false), ct).ConfigureAwait(false);
                sent[v.PointId] = v.Datetime;
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

    private static void Count(string kind) =>
        BuildingOsMetrics.TelemetryFeedEventsSent.Add(1, new KeyValuePair<string, object?>("kind", kind));
}
