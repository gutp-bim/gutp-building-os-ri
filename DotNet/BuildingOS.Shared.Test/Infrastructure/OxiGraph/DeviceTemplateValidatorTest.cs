using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

public class DeviceTemplateValidatorTest
{
    private static readonly DeviceTemplate SensorTemplate = new(
        Namespace: "ns",
        DeviceType: "Sensor",
        ClassName: "Sensor",
        Properties:
        [
            new("Temperature", "read", "Temperature"),
            new("Humidity", "read", "Humidity"),
        ]);

    // ── Cycle 3: empty store ──────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyRows_ReturnsNoErrors()
    {
        var errors = DeviceTemplateValidator.Validate(
            [SensorTemplate],
            rows: []);

        Assert.Empty(errors);
    }

    // ── Cycle 4: equipment matches template ───────────────────────────────

    [Fact]
    public void Validate_EquipmentMatchesTemplate_ReturnsNoErrors()
    {
        var rows = new[]
        {
            Row("DEV001", "Sensor", "Temperature"),
            Row("DEV001", "Sensor", "Humidity"),
        };

        var errors = DeviceTemplateValidator.Validate([SensorTemplate], rows);

        Assert.Empty(errors);
    }

    // ── Cycle 5: missing point ────────────────────────────────────────────

    [Fact]
    public void Validate_MissingPoint_ReturnsError()
    {
        var rows = new[]
        {
            Row("DEV001", "Sensor", "Temperature"),
            // Humidity is missing
        };

        var errors = DeviceTemplateValidator.Validate([SensorTemplate], rows);

        Assert.Single(errors);
        Assert.Equal("DEV001", errors[0].EquipmentId);
        Assert.Equal("Sensor", errors[0].DeviceType);
        Assert.Contains("Humidity", errors[0].MissingPointTypes);
    }

    // ── Cycle 6: device type not in templates ─────────────────────────────

    [Fact]
    public void Validate_UnknownDeviceType_IsIgnored()
    {
        // Equipment with deviceType not in the provided templates
        var rows = new[]
        {
            Row("DEV999", "UnknownType", "SomePoint"),
        };

        var errors = DeviceTemplateValidator.Validate([SensorTemplate], rows);

        Assert.Empty(errors);
    }

    // ── Cycle 7: multiple equipment instances ─────────────────────────────

    [Fact]
    public void Validate_MultipleEquipment_ReportsEachMismatch()
    {
        var rows = new[]
        {
            Row("DEV001", "Sensor", "Temperature"),
            Row("DEV001", "Sensor", "Humidity"),
            Row("DEV002", "Sensor", "Temperature"),
            // DEV002 is missing Humidity
        };

        var errors = DeviceTemplateValidator.Validate([SensorTemplate], rows);

        Assert.Single(errors);
        Assert.Equal("DEV002", errors[0].EquipmentId);
        Assert.Contains("Humidity", errors[0].MissingPointTypes);
    }

    private static IReadOnlyDictionary<string, string> Row(
        string equipmentId, string deviceType, string pointType)
        => new Dictionary<string, string>
        {
            ["equipmentId"] = equipmentId,
            ["deviceType"] = deviceType,
            ["pointType"] = pointType,
        };
}
