using System.Text.Json;
using BuildingOS.GatewayBridge.Mapping;
using BuildingOS.GatewayBridge.Protos;
using BuildingOS.Shared.Domain;

namespace BuildingOS.GatewayBridge.Test;

public class ControlCommandMapperTest
{
    private static string PointControlInfoJson(Guid id, string pointId, string body)
        => JsonSerializer.Serialize(new PointControlInfo
        {
            id = id, PointId = pointId, Type = DeviceControlType.BacnetSim, Body = body,
        });

    [Fact]
    public void ToControlCommand_MapsPointIdAndValue_ToControlCommand()
    {
        // point-id canonical (#181): the gateway resolves point_id → BACnet object/instance from the
        // shared point list, so the command carries only control_id + point_id + value (+ priority).
        var id = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new { value = 23.0, priority = 8 });

        var command = ControlCommandMapper.ToControlCommand(PointControlInfoJson(id, "PT001", body));

        Assert.NotNull(command);
        Assert.Equal(id.ToString(), command!.ControlId);
        Assert.Equal("PT001", command.PointId);
        Assert.Equal(23.0, command.PresentValue);
        Assert.Equal(8, command.Priority);
    }

    [Fact]
    public void ToControlCommand_DefaultsPriorityToZero_WhenAbsent()
    {
        var body = JsonSerializer.Serialize(new { value = 1.5 });
        var command = ControlCommandMapper.ToControlCommand(PointControlInfoJson(Guid.NewGuid(), "PT001", body));

        Assert.NotNull(command);
        Assert.Equal(1.5, command!.PresentValue);
        Assert.Equal(0, command.Priority);
    }

    [Fact]
    public void ToControlCommand_ReturnsNull_ForUnparseableJson()
    {
        Assert.Null(ControlCommandMapper.ToControlCommand("not json"));
    }

    [Fact]
    public void ToControlCommand_ReturnsNull_WhenBodyEmpty()
    {
        var json = PointControlInfoJson(Guid.NewGuid(), "PT001", "");
        Assert.Null(ControlCommandMapper.ToControlCommand(json));
    }

    [Fact]
    public void ToControlCommand_ReturnsNull_WhenPointIdMissing()
    {
        // Without a point_id the gateway cannot resolve the target — not routable.
        var body = JsonSerializer.Serialize(new { value = 1.0 });
        Assert.Null(ControlCommandMapper.ToControlCommand(PointControlInfoJson(Guid.NewGuid(), "", body)));
    }

    [Fact]
    public void ToControlCommand_ReturnsNull_WhenValueMissing()
    {
        // An absent value must be rejected, not silently written as 0 (0 is a legitimate setpoint).
        var body = JsonSerializer.Serialize(new { priority = 8 });
        Assert.Null(ControlCommandMapper.ToControlCommand(PointControlInfoJson(Guid.NewGuid(), "PT001", body)));
    }

    [Fact]
    public void ToResultJson_ProducesResultBusShape()
    {
        var json = ControlCommandMapper.ToResultJson(new ControlResult { ControlId = "x", Success = true, Response = "ok" });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ok", doc.RootElement.GetProperty("response").GetString());
    }
}
