namespace BuildingOS.Shared.Infrastructure.OxiGraph;

public static class DeviceTemplateValidator
{
    // Queries all equipment→point relationships from OxiGraph.
    // sbco:deviceType is on EquipmentExt (not PointExt) per the SBCO ontology (minCard: 0, maxCard: 1).
    // OPTIONAL is used because deviceType is optional in SBCO; equipment without it is excluded from
    // template validation (no template match → skipped in Validate()).
    private const string SparqlQuery = """
        PREFIX sbco: <https://www.sbco.or.jp/ont/>
        SELECT ?equipmentId ?deviceType ?pointType WHERE {
          ?equipment a sbco:EquipmentExt ;
                     sbco:id ?equipmentId ;
                     sbco:hasPoint ?point .
          OPTIONAL { ?equipment sbco:deviceType ?deviceType }
          ?point a sbco:PointExt ;
                 sbco:pointType ?pointType .
        }
        """;

    public static DeviceTemplateValidationError[] Validate(
        IReadOnlyList<DeviceTemplate> templates,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var duplicates = templates
            .GroupBy(t => t.DeviceType)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicates.Length > 0)
            throw new ArgumentException(
                $"Device templates contain duplicate DeviceType(s): [{string.Join(", ", duplicates)}]. " +
                "Use a single template per DeviceType or ensure each ZIP namespace uses unique DeviceType names.");

        var templateByDeviceType = templates.ToDictionary(t => t.DeviceType);

        // Group rows by (equipmentId, deviceType) and collect present pointTypes
        var equipment = rows
            .GroupBy(r => (
                EquipmentId: r.GetValueOrDefault("equipmentId", string.Empty),
                DeviceType: r.GetValueOrDefault("deviceType", string.Empty)))
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.GetValueOrDefault("pointType", string.Empty)).ToHashSet());

        var errors = new List<DeviceTemplateValidationError>();
        foreach (var (key, actualPoints) in equipment)
        {
            if (!templateByDeviceType.TryGetValue(key.DeviceType, out var template)) continue;
            var required = template.Properties.Select(p => p.PointType).ToHashSet();
            var missing = required.Except(actualPoints).ToArray();
            if (missing.Length > 0)
                errors.Add(new DeviceTemplateValidationError(key.EquipmentId, key.DeviceType, missing));
        }
        return errors.ToArray();
    }

    public static async Task<DeviceTemplateValidationError[]> ValidateAsync(
        IReadOnlyList<DeviceTemplate> templates,
        OxiGraphClient client,
        CancellationToken ct = default)
    {
        var rows = await client.QueryAsync(SparqlQuery, ct).ConfigureAwait(false);
        return Validate(templates, rows);
    }
}
