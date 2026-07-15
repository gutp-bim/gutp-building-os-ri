using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Infrastructure.ControlRouting;

namespace BuildingOS.Shared.Test.Infrastructure.ControlRouting;

public class ControlRequestRoutingTest
{
    [Fact]
    public void SubjectFor_RoutesBacnetSim_ToPerGatewaySubject()
    {
        Assert.Equal("building-os.control.request.gw.gw-1",
            ControlRequestRouting.SubjectFor(DeviceControlType.BacnetSim, "gw-1"));
    }

    [Fact]
    public void SubjectFor_RoutesHono_ToGenericSubject()
    {
        Assert.Equal(EgressSubjects.GenericRequest,
            ControlRequestRouting.SubjectFor(DeviceControlType.Hono, "gw-1"));
    }

    [Fact]
    public void SubjectFor_RoutesKandt_ToGenericSubject()
    {
        Assert.Equal(EgressSubjects.GenericRequest,
            ControlRequestRouting.SubjectFor(DeviceControlType.Kandt, null));
    }

    [Fact]
    public void SubjectFor_RoutesSimulated_ToGenericSubject()
    {
        Assert.Equal(EgressSubjects.GenericRequest,
            ControlRequestRouting.SubjectFor(DeviceControlType.Simulated, "gw-1"));
    }

    [Fact]
    public void SubjectFor_FallsBackToGeneric_ForBacnetSimWithoutGateway()
    {
        // Without a gatewayId there is no per-gateway subject to route to.
        Assert.Equal(EgressSubjects.GenericRequest,
            ControlRequestRouting.SubjectFor(DeviceControlType.BacnetSim, null));
    }

    // ── liveness-probe gating (#186) ────────────────────────────────────────

    [Fact]
    public void IsPerGatewayEgress_True_ForBacnetSimWithGateway()
    {
        // Only the per-gateway egress path can silently drop on an offline gateway → needs the probe.
        Assert.True(ControlRequestRouting.IsPerGatewayEgress(DeviceControlType.BacnetSim, "gw-1"));
    }

    [Theory]
    [InlineData(DeviceControlType.BacnetSim, null)] // no gateway → generic
    [InlineData(DeviceControlType.Hono, "gw-1")]    // durable in-process path
    [InlineData(DeviceControlType.Kandt, "gw-1")]
    [InlineData(DeviceControlType.Simulated, "gw-1")]
    public void IsPerGatewayEgress_False_ForDurableOrUnroutable(string controlType, string? gatewayId)
    {
        Assert.False(ControlRequestRouting.IsPerGatewayEgress(controlType, gatewayId));
    }
}
