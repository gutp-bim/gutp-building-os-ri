using BuildingOS.Shared;
using BuildingOs.ApiServer.GatewayProvisioning;

namespace BuildingOS.ApiServer.Test.GatewayProvisioning;

/// <summary>
/// Pure tests for the gateway point-list wire DTO mapping, in particular the split between the
/// canonical top-level <see cref="GatewayPointDto.Protocol"/> and the BACnet-only
/// <see cref="NativeAddressingDto"/> block.
/// </summary>
public class GatewayPointDtoTest
{
    [Fact]
    public void From_SurfacesResolvedProtocolAtTopLevel()
    {
        var dto = GatewayPointDto.From(new GatewayPointEntry
        {
            PointId = "PT001",
            LocalId = "sensors/room1/temp",
            Protocol = "mqtt",
        });

        Assert.Equal("mqtt", dto.Protocol);
        // No BACnet addressing → no native block at all; the top-level field is the only signal.
        Assert.Null(dto.Native);
    }

    [Fact]
    public void From_NativeProtocolStaysBacnet_EvenWhenResolvedProtocolDiffers()
    {
        // A point can carry BACnet addressing while an explicit bos:protocol resolves the canonical
        // protocol to something else (e.g. a simulator binding). The native block describes BACnet
        // object identity, so its protocol must stay "bacnet" — clients validating
        // native.protocol == "bacnet" must not break — while the top-level field carries the truth.
        var dto = GatewayPointDto.From(new GatewayPointEntry
        {
            PointId = "PT002",
            Protocol = "bacnet-sim",
            BacnetDeviceId = "BAC001",
            BacnetObjectType = "analogInput",
            BacnetInstanceNo = "1001",
        });

        Assert.Equal("bacnet-sim", dto.Protocol);
        Assert.NotNull(dto.Native);
        Assert.Equal("bacnet", dto.Native!.Protocol);
        Assert.Equal("BAC001", dto.Native.DeviceId);
        Assert.Equal("analogInput", dto.Native.ObjectType);
        Assert.Equal("1001", dto.Native.InstanceNo);
    }

    [Fact]
    public void From_UnresolvedProtocolIsNull_AndOmitsNativeBlock()
    {
        var dto = GatewayPointDto.From(new GatewayPointEntry { PointId = "PT003" });

        Assert.Null(dto.Protocol);
        Assert.Null(dto.Native);
    }
}
