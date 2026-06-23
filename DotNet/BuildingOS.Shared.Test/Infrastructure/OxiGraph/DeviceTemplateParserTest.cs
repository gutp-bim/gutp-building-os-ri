using System.IO.Compression;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

public class DeviceTemplateParserTest
{
    // ── Cycle 1: JSON format ──────────────────────────────────────────────

    [Fact]
    public void ParseJson_ReturnsSingleTemplate()
    {
        const string json = """
            [{"namespace":"ns","deviceType":"Sensor","className":"Sensor",
              "properties":[{"name":"Temperature","access":"read","pointType":"Temperature"}]}]
            """;
        var templates = DeviceTemplateParser.ParseJson(json);

        Assert.Single(templates);
        Assert.Equal("ns", templates[0].Namespace);
        Assert.Equal("Sensor", templates[0].DeviceType);
        Assert.Equal("Sensor", templates[0].ClassName);
        Assert.Single(templates[0].Properties);
        Assert.Equal("Temperature", templates[0].Properties[0].Name);
        Assert.Equal("read", templates[0].Properties[0].Access);
        Assert.Equal("Temperature", templates[0].Properties[0].PointType);
    }

    [Fact]
    public void ParseJson_ReturnsMultipleTemplates()
    {
        const string json = """
            [
              {"namespace":"ns","deviceType":"Sensor","className":"Sensor",
               "properties":[{"name":"Temp","access":"read","pointType":"Temperature"}]},
              {"namespace":"ns","deviceType":"HVAC","className":"AirConditioner",
               "properties":[
                 {"name":"SetTemp","access":"readWrite","pointType":"SetTemperature"},
                 {"name":"Mode","access":"readWrite","pointType":"OperationMode"}
               ]}
            ]
            """;
        var templates = DeviceTemplateParser.ParseJson(json);

        Assert.Equal(2, templates.Length);
        Assert.Equal("HVAC", templates[1].DeviceType);
        Assert.Equal(2, templates[1].Properties.Length);
        Assert.Equal("readWrite", templates[1].Properties[0].Access);
    }

    // ── Cycle 2: ZIP format ───────────────────────────────────────────────

    [Fact]
    public void ParseZip_InfersNamespaceAndDeviceTypeFromPath()
    {
        const string yaml = """
            className: "Sensor"
            properties:
              - name: "Temperature"
                access: "read"
                pointType: "Temperature"
            """;
        var zipBytes = BuildZip("templates/myns/Sensor.yaml", yaml);

        var templates = DeviceTemplateParser.ParseZip(zipBytes);

        Assert.Single(templates);
        Assert.Equal("myns", templates[0].Namespace);
        Assert.Equal("Sensor", templates[0].DeviceType);
        Assert.Equal("Sensor", templates[0].ClassName);
        Assert.Single(templates[0].Properties);
        Assert.Equal("Temperature", templates[0].Properties[0].PointType);
    }

    [Fact]
    public void ParseZip_ReturnsMultipleTemplates()
    {
        const string yaml1 = """
            className: "Sensor"
            properties:
              - name: "Temperature"
                access: "read"
                pointType: "Temperature"
            """;
        const string yaml2 = """
            className: "AirConditioner"
            properties:
              - name: "SetTemp"
                access: "readWrite"
                pointType: "SetTemperature"
              - name: "OnOff"
                access: "readWrite"
                pointType: "Status"
            """;
        var zipBytes = BuildZip(
            ("templates/ns/Sensor.yaml", yaml1),
            ("templates/ns/HVAC.yaml", yaml2));

        var templates = DeviceTemplateParser.ParseZip(zipBytes);

        Assert.Equal(2, templates.Length);
        var hvac = templates.Single(t => t.DeviceType == "HVAC");
        Assert.Equal(2, hvac.Properties.Length);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static byte[] BuildZip(string entryName, string content)
        => BuildZip((entryName, content));

    private static byte[] BuildZip(params (string name, string content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }
}
