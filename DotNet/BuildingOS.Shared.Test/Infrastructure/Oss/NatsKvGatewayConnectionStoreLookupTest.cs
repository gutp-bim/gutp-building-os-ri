using BuildingOS.Shared.Domain.Types;
using BuildingOS.Shared.Infrastructure.Oss;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NATS.Client.JetStream;

namespace BuildingOS.Shared.Test.Infrastructure.Oss;

/// <summary>
/// #463: the read must distinguish "this gateway has no live heartbeat" from "we could not find out".
/// Before this, both came back as <c>null</c>, and every caller read that as disconnected — so a KV
/// hiccup made `/health` report every missing point as ゲートウェイ切断 and `/admin/gateways` show
/// every gateway as 未接続.
///
/// The KV round-trip itself (a real heartbeat → Connected, a TTL-expired entry → Disconnected) is
/// covered against real NATS in <c>BuildingOS.IntegrationTest</c>; what is unit-testable here is the
/// failure arm, which needs no broker.
/// </summary>
public class NatsKvGatewayConnectionStoreLookupTest
{
    private static NatsKvGatewayConnectionStore UnreachableStore() => new(
        // A bare mock: opening the KV bucket off it fails, standing in for an unreachable NATS.
        new Mock<INatsJSContext>().Object,
        NullLogger<NatsKvGatewayConnectionStore>.Instance);

    [Fact]
    public async Task Get_WhenTheKvCannotBeRead_IsUnknown_NotDisconnected()
    {
        var lookup = await UnreachableStore().GetAsync("GW-001");

        Assert.Equal(GatewayConnectionState.Unknown, lookup.State);
        Assert.Null(lookup.Status);
    }

    [Fact]
    public async Task Get_WhenTheKvCannotBeRead_StillDoesNotThrow()
    {
        // The interface contract is best-effort: a KV problem must never break the egress stream or
        // the admin read path. Unknown is how "could not find out" is reported without throwing.
        var ex = await Record.ExceptionAsync(() => UnreachableStore().GetAsync("GW-001"));

        Assert.Null(ex);
    }

    [Fact]
    public void Lookup_Disconnected_AndUnknown_AreDistinctValues()
    {
        // Both carry a null Status, so only State separates them — if a caller ever switches on
        // `Status is null` again it is back to the #463 bug.
        Assert.NotEqual(GatewayConnectionLookup.Disconnected, GatewayConnectionLookup.Unknown);
        Assert.Null(GatewayConnectionLookup.Disconnected.Status);
        Assert.Null(GatewayConnectionLookup.Unknown.Status);
    }

    [Fact]
    public void Lookup_Live_CarriesTheStatus()
    {
        var status = new GatewayConnectionStatus("replica-1", DateTimeOffset.UnixEpoch, "\"sha256:abc\"");

        var lookup = GatewayConnectionLookup.Live(status);

        Assert.Equal(GatewayConnectionState.Connected, lookup.State);
        Assert.Same(status, lookup.Status);
    }

    [Theory]
    [InlineData(GatewayConnectionState.Connected, true)]
    [InlineData(GatewayConnectionState.Disconnected, false)]
    [InlineData(GatewayConnectionState.Unknown, null)]
    public void State_MapsToTheNullableBoolTheApiExposes(GatewayConnectionState state, bool? expected)
    {
        // The wire shape is `bool?` (null = 不明), matching GatewayAdminView.PointlistSynced next to it.
        Assert.Equal(expected, state.ToConnectedFlag());
    }
}
