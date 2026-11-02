using System.Globalization;
using BuildingOS.Shared.Domain.GatewayProvisioning;
using BuildingOS.Shared.Domain.TwinAdmin;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// A small, typed projection of clock resources and their equipment/point scope. RDF term kind and
/// datatype are retained explicitly: a literal spelling an IRI is never accepted as a reference.
/// Used by preview, guarded ingestion and the single-query provisioning read.
/// </summary>
internal sealed class ClockConfigurationGraph
{
    internal const string Bos = "http://buildingos.gutp.jp/ontology#";
    internal const string Sbco = "https://www.sbco.or.jp/ont/";
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";
    private sealed record Term(string Value, string Kind, string Datatype);
    private readonly Dictionary<string, Dictionary<string, HashSet<Term>>> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Predicate, string Object), HashSet<string>> _references = new();
    private readonly HashSet<string> _iriSubjects = new(StringComparer.Ordinal);

    internal ClockConfigurationGraph(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        foreach (var row in rows)
        {
            var s = row.GetValueOrDefault("s", "");
            var p = row.GetValueOrDefault("p", "");
            if (s.Length == 0 || p.Length == 0) continue;
            if (!_nodes.TryGetValue(s, out var props)) _nodes[s] = props = new(StringComparer.Ordinal);
            if (!props.TryGetValue(p, out var terms)) props[p] = terms = [];
            var term = new Term(row.GetValueOrDefault("o", ""), row.GetValueOrDefault("kind", ""), row.GetValueOrDefault("datatype", ""));
            terms.Add(term);
            if (term.Kind == "iri")
            {
                var key = (p, term.Value);
                if (!_references.TryGetValue(key, out var subjects)) _references[key] = subjects = new(StringComparer.Ordinal);
                subjects.Add(s);
            }
            if (row.GetValueOrDefault("subjectIri") == "true") _iriSubjects.Add(s);
        }
    }

    // A single SELECT runs against one OxiGraph read transaction. All clock-related resources are
    // included for validation; the optional gateway adds only that gateway's legacy points/devices.
    internal static string BuildQuery(string? graph = null, TwinImportMode mode = TwinImportMode.Replace, string? gatewayId = null)
    {
        string L(string triple) => graph is null ? triple : OxiGraphTwinAdminService.Link(graph, mode, triple);
        var branches = new List<string>
        {
            L("?s a bos:ClockSource ."), L("?s a bos:BacnetClockBinding ."),
            L("?s bos:usesClockSource ?clock ."), L("?equipment bos:usesClockSource ?s ."),
            L("?clock bos:nativeBinding ?s ."),
            L("?equipment bos:usesClockSource ?clock .") + L("?equipment sbco:hasPoint ?s ."),
            L("?equipment bos:usesClockSource ?clock .") + L("?equipment sbco:hasPoint ?point .") + L("?s sbco:hasPoint ?point ."),
            // Include duplicate IDs even when the other point is outside the equipment's scope.
            L("?equipment bos:usesClockSource ?clock .") + L("?equipment sbco:hasPoint ?point .") + L("?point sbco:id ?id .") + L("?s a sbco:PointExt ; sbco:id ?id ."),
        };
        if (gatewayId is not null)
        {
            // Use a proper short-string literal escape; never interpolate an unescaped gateway ID.
            var value = gatewayId.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
            var point = $"?point a sbco:PointExt ; sbco:gatewayId \"{value}\" .";
            branches.Add(L($"?s a sbco:PointExt ; sbco:gatewayId \"{value}\" ."));
            branches.Add(L(point) + L("?s sbco:hasPoint ?point ."));
        }
        return $$"""
            PREFIX bos: <{{Bos}}>
            PREFIX sbco: <{{Sbco}}>
            SELECT DISTINCT ?s ?p ?o (isIRI(?s) AS ?subjectIri)
              (IF(isIRI(?o), "iri", IF(isBlank(?o), "blank", "literal")) AS ?kind)
              (DATATYPE(?o) AS ?datatype)
            WHERE {
              { SELECT DISTINCT ?s WHERE { {{string.Join(" UNION ", branches.Select(b => "{ " + b + " }"))}} } }
              {{L("?s ?p ?o .")}}
            }
            """;
    }

    private IEnumerable<Term> Values(string s, string p) =>
        _nodes.TryGetValue(s, out var props) && props.TryGetValue(p, out var terms) ? terms : [];
    private string? Value(string s, string p) => Values(s, p).OrderBy(t => t.Value, StringComparer.Ordinal).FirstOrDefault()?.Value;
    private bool Typed(string s, string type) => Values(s, RdfType).Any(t => t.Kind == "iri" && t.Value == type);
    private IEnumerable<string> OfType(string type) => _nodes.Keys.Where(s => Typed(s, type));
    private IEnumerable<string> Referencing(string p, string target) => _references.TryGetValue((p, target), out var subjects) ? subjects : [];

    internal IReadOnlyList<TwinClockIssue> Validate()
    {
        var issues = new List<TwinClockIssue>();
        var pointCountsById = OfType(Sbco + "PointExt").Select(p => Value(p, Sbco + "id") ?? "")
            .GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        void Issue(string s, string reason) => issues.Add(new(s, reason));
        bool Single(string s, string p, string kind, string? datatype = null, string? expected = null)
        {
            var values = Values(s, p).ToArray();
            return values.Length == 1 && values[0].Kind == kind &&
                (datatype is null || values[0].Datatype == Xsd + datatype) &&
                (expected is null || values[0].Value == expected);
        }
        void Closed(string s, params string[] properties)
        {
            var allowed = properties.Select(p => Bos + p).Append(RdfType).ToHashSet(StringComparer.Ordinal);
            if (_nodes[s].Keys.Any(p => !allowed.Contains(p))) Issue(s, "unexpected_static_property");
        }
        foreach (var s in OfType(Bos + "ClockSource"))
        {
            if (!_iriSubjects.Contains(s)) Issue(s, "clock_not_iri");
            if (Typed(s, Sbco + "PointExt")) Issue(s, "clock_is_point");
            Closed(s, "clockDomain", "clockUnit", "readOnly", "acquisitionMode", "nativeBinding");
            if (!Single(s, Bos + "clockDomain", "literal", "string", "simulation") ||
                !Single(s, Bos + "clockUnit", "literal", "string", "s") ||
                !Single(s, Bos + "acquisitionMode", "literal", "string", "poll") ||
                !Single(s, Bos + "readOnly", "literal", "boolean") || Value(s, Bos + "readOnly") is not ("true" or "1"))
                Issue(s, "invalid_clock_semantics");
            if (!Single(s, Bos + "nativeBinding", "iri") || !Typed(Value(s, Bos + "nativeBinding") ?? "", Bos + "BacnetClockBinding"))
                Issue(s, "invalid_native_binding");
        }
        foreach (var s in OfType(Bos + "BacnetClockBinding"))
        {
            if (!_iriSubjects.Contains(s)) Issue(s, "native_not_iri");
            Closed(s, "gatewayId", "connectorId", "deviceInstance", "objectType", "objectInstance", "property", "nativeUnit");
            foreach (var prop in new[] { "gatewayId", "connectorId" })
                if (!Single(s, Bos + prop, "literal", "string") || string.IsNullOrWhiteSpace(Value(s, Bos + prop))) Issue(s, "invalid_" + prop);
            foreach (var (prop, max) in new[] { ("deviceInstance", 4194302), ("objectInstance", 4194303) })
                if (!Single(s, Bos + prop, "literal", "integer") || !int.TryParse(Value(s, Bos + prop), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0 || n > max)
                    Issue(s, "invalid_" + prop);
            if (!Single(s, Bos + "objectType", "literal", "string") || Value(s, Bos + "objectType") is not ("analogValue" or "analogInput") ||
                !Single(s, Bos + "property", "literal", "string", "presentValue") || !Single(s, Bos + "nativeUnit", "literal", "string", "seconds"))
                Issue(s, "invalid_native_source");
        }
        foreach (var eq in _nodes.Keys.Where(s => Values(s, Bos + "usesClockSource").Any()))
        {
            if (!_iriSubjects.Contains(eq) || !Typed(eq, Sbco + "EquipmentExt") || Typed(eq, Sbco + "PointExt")) Issue(eq, "invalid_equipment");
            // GatewayPointDto's device grouping requires the equipment's business ID. Without
            // it a valid-looking binding would have no corresponding points[].device.dtId.
            if (!Single(eq, Sbco + "id", "literal", "string") || string.IsNullOrWhiteSpace(Value(eq, Sbco + "id"))) Issue(eq, "invalid_equipment_id");
            if (!Single(eq, Bos + "usesClockSource", "iri")) { Issue(eq, "multiple_or_invalid_clocks"); continue; }
            var clock = Value(eq, Bos + "usesClockSource")!;
            if (!Typed(clock, Bos + "ClockSource")) { Issue(eq, "clock_missing"); continue; }
            var native = Value(clock, Bos + "nativeBinding") ?? "";
            var gateway = Value(native, Bos + "gatewayId");
            var points = Values(eq, Sbco + "hasPoint").ToArray();
            if (points.Length == 0) Issue(eq, "equipment_has_no_points");
            foreach (var point in points)
            {
                var pt = point.Value;
                if (point.Kind != "iri" || !Typed(pt, Sbco + "PointExt")) { Issue(eq, "point_missing"); continue; }
                if (!Single(pt, Sbco + "gatewayId", "literal", "string", gateway) || gateway is null) Issue(pt, "gateway_mismatch");
                if (!Single(pt, Sbco + "id", "literal", "string") || string.IsNullOrWhiteSpace(Value(pt, Sbco + "id"))) Issue(pt, "invalid_point_id");
                if (Value(pt, Sbco + "id") == "BPT-0001") Issue(pt, "retired_point_id");
                if (Referencing(Sbco + "hasPoint", pt).Count() != 1) Issue(pt, "multiple_equipment");
                if (pointCountsById.GetValueOrDefault(Value(pt, Sbco + "id") ?? "") != 1) Issue(pt, "duplicate_point_id");
            }
        }
        return issues.Distinct().OrderBy(i => i.ResourceId, StringComparer.Ordinal).ThenBy(i => i.Reason, StringComparer.Ordinal).ToArray();
    }

    internal IReadOnlyList<TwinClockIssue> ValidateOwnership(ClockConfigurationGraph previous)
    {
        return OfType(Bos + "ClockSource").Where(c => previous.Typed(c, Bos + "ClockSource"))
            .Where(c => Value(Value(c, Bos + "nativeBinding") ?? "", Bos + "gatewayId") !=
                        previous.Value(previous.Value(c, Bos + "nativeBinding") ?? "", Bos + "gatewayId"))
            .Select(c => new TwinClockIssue(c, "clock_owner_changed")).ToArray();
    }

    internal GatewayProvisioningSnapshot Project(string gatewayId)
    {
        var issues = Validate();
        if (issues.Count > 0) throw new ClockConfigurationException(issues);
        var clocks = OfType(Bos + "ClockSource").Where(c => Value(Value(c, Bos + "nativeBinding")!, Bos + "gatewayId") == gatewayId)
            .Order(StringComparer.Ordinal).Select(c =>
            {
                var n = Value(c, Bos + "nativeBinding")!;
                return new GatewayClockSource(c, "simulation", "s", true, "poll", new(gatewayId,
                    Value(n, Bos + "connectorId")!, int.Parse(Value(n, Bos + "deviceInstance")!, CultureInfo.InvariantCulture),
                    Value(n, Bos + "objectType")!, int.Parse(Value(n, Bos + "objectInstance")!, CultureInfo.InvariantCulture), "presentValue", "seconds"));
            }).ToArray();
        var clockIds = clocks.Select(c => c.ClockId).ToHashSet(StringComparer.Ordinal);
        var bindings = _nodes.Keys.Where(e => clockIds.Contains(Value(e, Bos + "usesClockSource") ?? ""))
            .Order(StringComparer.Ordinal).Select(e => new EquipmentClockBinding(e, Value(e, Bos + "usesClockSource")!)).ToArray();
        var points = OfType(Sbco + "PointExt").Where(p => Value(p, Sbco + "gatewayId") == gatewayId && Value(p, Sbco + "id") is not null)
            .OrderBy(p => Value(p, Sbco + "id"), StringComparer.Ordinal).Select(p =>
            {
                string? S(string prop) => Value(p, Sbco + prop);
                string? B(string prop) => Value(p, Bos + prop);
                var eq = Referencing(Sbco + "hasPoint", p).Where(e => Typed(e, Sbco + "EquipmentExt") && Value(e, Sbco + "id") is not null).Order(StringComparer.Ordinal).FirstOrDefault();
                return new GatewayPointEntry
                {
                    PointId = S("id") ?? "", LocalId = S("localId"), Unit = S("unit"),
                    BacnetDeviceId = S("deviceIdBacnet"), BacnetObjectType = S("objectTypeBacnet"), BacnetInstanceNo = S("instanceNoBacnet"),
                    Writable = S("writable") is { } w ? w == "true" : null,
                    Protocol = GatewayPointProtocolResolver.Resolve(B("protocol"), S("deviceIdBacnet"), S("objectTypeBacnet"), S("instanceNoBacnet"), S("localId")),
                    DataType = B("dataType"), MinValue = B("minValue"), MaxValue = B("maxValue"), EnumLabels = B("enumLabels"),
                    DeviceDtId = eq, DeviceId = eq is null ? null : Value(eq, Sbco + "id"), DeviceName = eq is null ? null : Value(eq, Sbco + "name"),
                    ReleaseOf = B("releaseOf") is { } target && Value(target, Sbco + "gatewayId") == gatewayId ? Value(target, Sbco + "id") : null,
                };
            }).ToArray();
        var byId = points.GroupBy(p => p.PointId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var p in points)
            if (p.ReleaseOf is { } target && byId.TryGetValue(target, out var t) && t.Protocol is not null) p.Protocol = t.Protocol;
        return new(points, clocks, bindings);
    }
}

public sealed class OxiGraphGatewayProvisioningSource(OxiGraphClient client) : IGatewayProvisioningSource
{
    public async Task<GatewayProvisioningSnapshot> ReadAsync(string gatewayId, CancellationToken ct)
    {
        var rows = await client.QueryAsync(ClockConfigurationGraph.BuildQuery(gatewayId: gatewayId), ct).ConfigureAwait(false);
        return new ClockConfigurationGraph(rows).Project(gatewayId);
    }
}
