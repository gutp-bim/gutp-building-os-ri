using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

public static class DeviceTemplateParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static DeviceTemplate[] ParseJson(string json)
    {
        var dtos = JsonSerializer.Deserialize<DeviceTemplateJsonDto[]>(json, JsonOptions)
                   ?? [];
        return dtos.Select(ToModel).ToArray();
    }

    public static DeviceTemplate[] ParseZip(byte[] zipBytes)
    {
        using var stream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var results = new List<DeviceTemplate>();

        foreach (var entry in archive.Entries)
        {
            // Expect path: templates/{namespace}/{deviceType}.yaml
            var parts = entry.FullName.TrimEnd('/').Split('/');
            if (parts.Length != 3 || parts[0] != "templates") continue;
            if (!parts[2].EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                && !parts[2].EndsWith(".yml", StringComparison.OrdinalIgnoreCase)) continue;

            var @namespace = parts[1];
            var deviceType = Path.GetFileNameWithoutExtension(parts[2]);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var yaml = reader.ReadToEnd();
            results.Add(ParseYaml(yaml, @namespace, deviceType));
        }

        return results.ToArray();
    }

    public static async Task<DeviceTemplate[]> LoadAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".json")
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return ParseJson(json);
        }
        if (ext == ".zip")
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
            return ParseZip(bytes);
        }
        throw new NotSupportedException($"Unsupported device template format: '{ext}'. Supported: .json, .zip");
    }

    // Parses the YAML format produced by serializeDeviceTemplate() in smartbuilding_datamodel_builder.
    // namespace and deviceType are NOT in the YAML body; they come from the ZIP entry path.
    internal static DeviceTemplate ParseYaml(string yaml, string @namespace, string deviceType)
    {
        var className = deviceType;
        var properties = new List<DeviceTemplatePropertyBuilder>();
        DeviceTemplatePropertyBuilder? current = null;
        bool inProperties = false;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            if (trimmed == "properties:")
            {
                inProperties = true;
                continue;
            }

            if (!inProperties)
            {
                var (key, value) = SplitKeyValue(trimmed);
                if (key == "className") className = value;
                continue;
            }

            if (trimmed.StartsWith("- "))
            {
                current = new DeviceTemplatePropertyBuilder();
                properties.Add(current);
                var (key, value) = SplitKeyValue(trimmed[2..]);
                current.Set(key, value);
                continue;
            }

            current?.Set(SplitKeyValue(trimmed));
        }

        return new DeviceTemplate(
            @namespace,
            deviceType,
            className,
            properties.Select(b => b.Build()).ToArray());
    }

    private static (string key, string value) SplitKeyValue(string line)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return (line.Trim(), string.Empty);
        var key = line[..idx].Trim();
        var raw = line[(idx + 1)..].Trim();
        // Strip surrounding quotes added by serializeDeviceTemplate's escape()
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return (key, raw);
    }

    private static void Set(this DeviceTemplatePropertyBuilder b, (string key, string value) kv)
        => b.Set(kv.key, kv.value);

    private static DeviceTemplate ToModel(DeviceTemplateJsonDto dto) =>
        new(
            dto.Namespace ?? string.Empty,
            dto.DeviceType ?? string.Empty,
            dto.ClassName ?? string.Empty,
            dto.Properties?.Select(p => new DeviceTemplateProperty(
                p.Name ?? string.Empty,
                p.Access ?? "read",
                p.PointType ?? string.Empty)).ToArray() ?? []);

    private sealed class DeviceTemplatePropertyBuilder
    {
        public string Name { get; private set; } = string.Empty;
        public string Access { get; private set; } = "read";
        public string PointType { get; private set; } = string.Empty;

        public void Set(string key, string value)
        {
            if (key == "name") Name = value;
            else if (key == "access") Access = value;
            else if (key == "pointType") PointType = value;
        }

        public DeviceTemplateProperty Build() => new(Name, Access, PointType);
    }
}

// DTOs for JSON deserialization only — not part of the public API
internal sealed class DeviceTemplateJsonDto
{
    public string? Namespace { get; set; }
    public string? DeviceType { get; set; }
    public string? ClassName { get; set; }
    public DeviceTemplatePropertyJsonDto[]? Properties { get; set; }
}

internal sealed class DeviceTemplatePropertyJsonDto
{
    public string? Name { get; set; }
    public string? Access { get; set; }
    public string? PointType { get; set; }
}
