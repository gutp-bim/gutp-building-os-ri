using BuildingOS.Shared.Entities;
using Corvus.Json;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Per-reading data extracted from a protocol-specific message before PointId resolution.
/// Used as the common intermediate type in ProtocolConnectorBase.ExtractReadingsAsync.
/// </summary>
public readonly record struct ProtocolReading(
    /// <summary>Key passed to IPointIdFactory.TryGetPointIdAsync.</summary>
    string LocalId,
    /// <summary>Numeric sensor value.</summary>
    JsonNumber Value,
    /// <summary>Timestamp from the parsed schema (JsonString or JsonDateTime).</summary>
    JsonAny Datetime,
    /// <summary>Human-readable name for the telemetry entity.</summary>
    string Name,
    /// <summary>Device identifier for the telemetry entity.</summary>
    string DeviceId,
    /// <summary>Protocol-specific metadata fields appended to the telemetry entity.</summary>
    ValidMessageJson.ValidTelemetryEntity.DataEntity Data
);
