namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// RDF namespace constants for OxiGraph SPARQL queries.
/// Primary namespace is SBCO (https://www.sbco.or.jp/ont/); bos: is retained only for
/// Building OS-specific extensions (ControlSchema, dataType, enumLabels) that have no SBCO equivalent.
/// </summary>
internal static class OxiGraphOntology
{
    private const string SbcoNs = "https://www.sbco.or.jp/ont/";
    private const string BosNs  = "http://buildingos.gutp.jp/ontology#";

    // SBCO class hierarchy: Building/Level/Room are subclasses of Architecture → Space (no Ext suffix).
    // EquipmentExt and PointExt carry the Ext suffix as SBCO extensions.
    internal const string Cls_Building  = SbcoNs + "Building";
    internal const string Cls_Level     = SbcoNs + "Level";
    internal const string Cls_Space     = SbcoNs + "Room";         // sbco:Room; sbco:SpaceExt does not exist
    internal const string Cls_Equipment = SbcoNs + "EquipmentExt";
    internal const string Cls_Point     = SbcoNs + "PointExt";

    // Building OS extension class (not in SBCO; used by ControlSchema queries)
    internal const string Cls_ControlSchema = BosNs + "ControlSchema";

    // SBCO properties
    internal const string Prop_Id        = SbcoNs + "id";
    internal const string Prop_Name      = SbcoNs + "name";
    internal const string Prop_HasPart   = SbcoNs + "hasPart";
    internal const string Prop_LocatedIn = SbcoNs + "locatedIn";
    internal const string Prop_HasPoint  = SbcoNs + "hasPoint";
    internal const string Prop_Writable  = SbcoNs + "writable";
    internal const string Prop_GatewayId = SbcoNs + "gatewayId";
    internal const string Prop_PointType = SbcoNs + "pointType";
    internal const string Prop_PointSpec = SbcoNs + "pointSpecification";
    // sbco:floor is a string literal on EquipmentExt used to join equipment to Level by name
    internal const string Prop_Floor     = SbcoNs + "floor";

    // Expected telemetry interval (seconds) on PointExt — drives per-point stale detection (#183).
    internal const string Prop_Interval         = SbcoNs + "interval";

    // Native addressing + unit on PointExt (used by the gateway point-list export, #224).
    internal const string Prop_Unit             = SbcoNs + "unit";
    internal const string Prop_LocalId          = SbcoNs + "localId";
    internal const string Prop_DeviceIdBacnet   = SbcoNs + "deviceIdBacnet";
    internal const string Prop_ObjectTypeBacnet = SbcoNs + "objectTypeBacnet";
    internal const string Prop_InstanceNoBacnet = SbcoNs + "instanceNoBacnet";

    // Descriptive PointExt properties the seeds have always carried but no query SELECTed (#298).
    // minPresValue/maxPresValue are the BACnet *raw* bounds of the object — NOT the control-write
    // range, which is bos:minValue/bos:maxValue on the ControlSchema (see Prop_MinValue below).
    internal const string Prop_Scale            = SbcoNs + "scale";
    internal const string Prop_TargetArea       = SbcoNs + "targetArea";
    internal const string Prop_InstallationArea = SbcoNs + "installationArea";
    internal const string Prop_MinPresValue     = SbcoNs + "minPresValue";
    internal const string Prop_MaxPresValue     = SbcoNs + "maxPresValue";

    // Descriptive equipment properties (#298). CSV-derived twins repeat them on every PointExt row
    // because the point list is the import unit, so the read paths accept either carrier.
    internal const string Prop_DeviceType = SbcoNs + "deviceType";
    internal const string Prop_Supplier   = SbcoNs + "supplier";
    internal const string Prop_Owner      = SbcoNs + "owner";
    internal const string Prop_Site       = SbcoNs + "site";

    // Building OS extension properties (not in SBCO; used by ControlSchema queries)
    internal const string Prop_DataType   = BosNs + "dataType";
    internal const string Prop_EnumLabels = BosNs + "enumLabels";
    // The canonical control-write range (#153). sbco:minPresValue/maxPresValue above describe the
    // BACnet object's raw span and are only a display fallback when no ControlSchema exists (#298).
    internal const string Prop_MinValue   = BosNs + "minValue";  // number-control lower bound (#153)
    internal const string Prop_MaxValue   = BosNs + "maxValue";  // number-control upper bound (#153)

    // Explicit protocol marker on PointExt (gateway point-list export). No SBCO equivalent — a point
    // reachable only via sbco:localId (MQTT/OPC-UA/Hono-style) has no other protocol signal on the wire.
    internal const string Prop_Protocol = BosNs + "protocol";

    // Opt-in per-point alarm thresholds on PointExt (#158 Phase 2a, ADR-0005). Distinct from the control
    // bounds above: these are the normal-operation value range, not the valid setpoint-write range.
    // alarm* = critical (outer) limits; warn* = the earlier inner limits. All optional/independent.
    internal const string Prop_AlarmHigh  = BosNs + "alarmHigh";
    internal const string Prop_AlarmLow   = BosNs + "alarmLow";
    internal const string Prop_WarnHigh   = BosNs + "warnHigh";
    internal const string Prop_WarnLow    = BosNs + "warnLow";

    // SBCO map-entry types/properties (#332). customTags is map(string -> boolean), identifiers is
    // map(string -> string); each entry is a resource/blank node carrying key + value.
    internal const string Cls_KeyBoolMapEntry   = SbcoNs + "KeyBoolMapEntry";
    internal const string Cls_KeyStringMapEntry = SbcoNs + "KeyStringMapEntry";
    internal const string Prop_CustomTags  = SbcoNs + "customTags";   // → KeyBoolMapEntry
    internal const string Prop_Identifiers = SbcoNs + "identifiers";  // → KeyStringMapEntry (future search)
    internal const string Prop_Key   = SbcoNs + "key";
    internal const string Prop_Value = SbcoNs + "value";

    // In SBCO the node URI IS the DtId — no transformation is needed.
    internal static string NodeUri(string dtId) => dtId;

    internal const string Prefixes = @"PREFIX sbco: <https://www.sbco.or.jp/ont/>
PREFIX bos: <http://buildingos.gutp.jp/ontology#>
PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
";
}
