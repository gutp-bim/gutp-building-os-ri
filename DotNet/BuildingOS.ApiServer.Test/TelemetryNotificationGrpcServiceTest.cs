using System.Security.Claims;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Protos;
using BuildingOs.ApiServer.Services;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>#636: the latest-value push stream — authorization, limits, snapshot then changes.</summary>
public class TelemetryNotificationGrpcServiceTest
{
    private static readonly DateTimeOffset T = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Grpc.AspNetCore's GetHttpContext() falls back to UserState["__HttpContext"] outside a real call.</summary>
    private sealed class FakeCallContext(HttpContext http, CancellationToken ct) : ServerCallContext
    {
        private readonly Dictionary<object, object> _state = new() { ["__HttpContext"] = http };
        protected override string MethodCore => "WatchLatest";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => ct;
        protected override Metadata ResponseTrailersCore => [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
        protected override IDictionary<object, object> UserStateCore => _state;
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }

    private sealed class Collector : IServerStreamWriter<LatestValueEvent>
    {
        public List<LatestValueEvent> Events { get; } = [];
        public TaskCompletionSource<bool>? WhenCount { get; set; }
        public int Target { get; set; } = int.MaxValue;
        public WriteOptions? WriteOptions { get; set; }
        // The service passes the call's token; the interface's default overload refuses a cancellable one.
        public Task WriteAsync(LatestValueEvent message, CancellationToken cancellationToken) => WriteAsync(message);

        public Task WriteAsync(LatestValueEvent message)
        {
            lock (Events)
            {
                Events.Add(message);
                if (Events.Count >= Target) WhenCount?.TrySetResult(true);
            }
            return Task.CompletedTask;
        }
    }

    private static HttpContext Http(bool authenticated = true, DateTimeOffset? exp = null)
    {
        var claims = new List<Claim> { new("sub", "u1") };
        if (exp is { } e) claims.Add(new Claim("exp", e.ToUnixTimeSeconds().ToString()));
        var http = new DefaultHttpContext
        {
            User = authenticated
                ? new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                : new ClaimsPrincipal(new ClaimsIdentity()),
        };
        http.Items["AuthorizationContext"] = new AuthorizationContext { UserId = "u1", Role = "operator", Permissions = [] };
        return http;
    }

    private static Mock<IAuthorizedTwinView> View(params string[] readable)
    {
        var view = new Mock<IAuthorizedTwinView>();
        view.Setup(v => v.GetPointAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuthorizationContext _, string id, CancellationToken _) => readable.Contains(id)
                ? new TwinGetResult<Point>.Ok(new Point { DtId = "urn:" + id, Id = id, Name = id })
                : new TwinGetResult<Point>.Forbidden());
        return view;
    }

    private static TelemetryNotificationGrpcService Service(LatestValueFeed feed, Mock<IAuthorizedTwinView> view) =>
        new(feed, view.Object, NullLogger<TelemetryNotificationGrpcService>.Instance);

    private static WatchLatestRequest Req(params string[] ids) { var r = new WatchLatestRequest(); r.PointIds.AddRange(ids); return r; }

    private static LatestValue V(string id, double value, string at) => new(id, value, null, null, "number", at, T);

    [Fact]
    public async Task Unauthenticated_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            Service(new LatestValueFeed(), View("P1")).WatchLatest(Req("P1"), new Collector(), new FakeCallContext(Http(false), default)));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task OneUnreadablePoint_RefusesTheWholeCall_WithoutNamingIt()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            Service(new LatestValueFeed(), View("P1")).WatchLatest(Req("P1", "SECRET"), new Collector(), new FakeCallContext(Http(), default)));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.DoesNotContain("SECRET", ex.Status.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TelemetryNotificationGrpcService.MaxPoints + 1)]
    public async Task NoPoints_OrTooMany_IsInvalidArgument(int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => $"P{i}").ToArray();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            Service(new LatestValueFeed(), View(ids)).WatchLatest(Req(ids), new Collector(), new FakeCallContext(Http(), default)));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task SendsTheSnapshot_ThenOnlyRealChanges_UntilTheClientLeaves()
    {
        var feed = new LatestValueFeed();
        feed.Publish(V("P1", 1, "2026-11-08T00:00:00Z"));
        using var cts = new CancellationTokenSource();
        var collector = new Collector { Target = 2, WhenCount = new(TaskCreationOptions.RunContinuationsAsynchronously) };

        var call = Service(feed, View("P1")).WatchLatest(Req("P1"), collector, new FakeCallContext(Http(), cts.Token));
        await WaitUntil(() => feed.SubscriberCount == 1);

        feed.Publish(V("P1", 1, "2026-11-08T00:00:00Z")); // a replayed, unchanged value is not a change
        feed.Publish(V("P1", 2, "2026-11-08T00:00:10Z"));
        await collector.WhenCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await call.WaitAsync(TimeSpan.FromSeconds(5)); // ends normally on client cancel

        Assert.Equal([(1d, true), (2d, false)], collector.Events.Select(e => (e.NumberValue, e.Snapshot)));
        Assert.Equal(0, feed.SubscriberCount);
    }

    [Theory]
    [InlineData("string", null, "on", null, LatestValueEvent.ValueOneofCase.StringValue)]
    [InlineData("boolean", 1.0, null, true, LatestValueEvent.ValueOneofCase.BoolValue)]
    [InlineData("number", 3.5, null, null, LatestValueEvent.ValueOneofCase.NumberValue)]
    [InlineData(null, 3.5, null, null, LatestValueEvent.ValueOneofCase.NumberValue)]
    // ADR-0006: a stale numeric beside a string reading is not the value.
    [InlineData("string", 9.0, null, null, LatestValueEvent.ValueOneofCase.None)]
    public void ToEvent_FollowsTheValueTypeDiscriminator(
        string? type, double? number, string? text, bool? flag, LatestValueEvent.ValueOneofCase expected)
    {
        var e = TelemetryNotificationGrpcService.ToEvent(new LatestValue("P1", number, text, flag, type, "t", T), snapshot: false);
        Assert.Equal(expected, e.ValueCase);
    }

    [Fact]
    public void Lifetime_IsFiveMinutes_CutShortByTheTokensExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        ClaimsPrincipal User(DateTimeOffset? exp) => Http(exp: exp).User;

        Assert.Equal(TelemetryNotificationGrpcService.MaxLifetime, TelemetryNotificationGrpcService.Lifetime(User(null), now));
        Assert.Equal(TelemetryNotificationGrpcService.MaxLifetime, TelemetryNotificationGrpcService.Lifetime(User(now.AddHours(1)), now));
        var soon = TelemetryNotificationGrpcService.Lifetime(User(now.AddSeconds(60)), now);
        Assert.InRange(soon.TotalSeconds, 59, 60);
        Assert.Equal(TimeSpan.Zero, TelemetryNotificationGrpcService.Lifetime(User(now.AddSeconds(-1)), now));
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            Service(new LatestValueFeed(), View("P1")).WatchLatest(
                Req("P1"), new Collector(), new FakeCallContext(Http(exp: DateTimeOffset.UtcNow.AddMinutes(-1)), default)));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task TheStreamEndsCleanly_WhenTheTokenExpires_SoTheClientMustReauthorize()
    {
        // A permission revoked (or a token expired) after the stream opened must stop the values within a bound.
        var feed = new LatestValueFeed();
        var call = Service(feed, View("P1")).WatchLatest(
            Req("P1"), new Collector(), new FakeCallContext(Http(exp: DateTimeOffset.UtcNow.AddSeconds(2)), default));

        await call.WaitAsync(TimeSpan.FromSeconds(10)); // returns, does not throw
        Assert.Equal(0, feed.SubscriberCount);
    }

    /// <summary>A client that never reads: every write blocks until its token is cancelled.</summary>
    private sealed class StuckWriter : IServerStreamWriter<LatestValueEvent>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(LatestValueEvent message) => Task.Delay(Timeout.Infinite);
        public Task WriteAsync(LatestValueEvent message, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    [Fact]
    public async Task ASlowClient_CannotHoldTheStreamPastItsLifetime()
    {
        var feed = new LatestValueFeed();
        feed.Publish(V("P1", 1, "2026-11-08T00:00:00Z")); // the snapshot write will block
        var call = Service(feed, View("P1")).WatchLatest(
            Req("P1"), new StuckWriter(), new FakeCallContext(Http(exp: DateTimeOffset.UtcNow.AddSeconds(2)), default));

        await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, feed.SubscriberCount);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
