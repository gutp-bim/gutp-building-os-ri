"""
Transforms a CosmosDB telemetry document to a TimescaleDB row dict.
Field mapping: CosmosDB → TimescaleDB telemetry table.
"""

from __future__ import annotations
from datetime import datetime, timezone
from typing import Any
import json


def cosmos_doc_to_row(doc: dict[str, Any]) -> dict[str, Any] | None:
    """
    Convert a CosmosDB telemetry document to a TimescaleDB insert dict.

    Returns None if the document cannot be mapped (e.g., missing datetime).
    """
    raw_time = doc.get("datetime") or doc.get("_ts")
    if raw_time is None:
        return None

    if isinstance(raw_time, (int, float)):
        ts = datetime.fromtimestamp(raw_time, tz=timezone.utc)
    else:
        ts = _parse_iso(str(raw_time))
        if ts is None:
            return None

    data_field = doc.get("data")
    if isinstance(data_field, str):
        try:
            data_field = json.loads(data_field)
        except ValueError:
            data_field = {"raw": data_field}

    return {
        "time": ts,
        "point_id": doc.get("point_id") or doc.get("pointId") or doc.get("id", ""),
        "building": doc.get("building"),
        "device_id": doc.get("device_id") or doc.get("deviceId"),
        "name": doc.get("name"),
        "value": _to_float(doc.get("value")),
        "data": data_field,
        "id": doc.get("id"),
    }


def is_warm(ts: datetime, cutoff: datetime) -> bool:
    """True if the timestamp falls within the warm window (>= cutoff)."""
    if ts.tzinfo is None:
        ts = ts.replace(tzinfo=timezone.utc)
    if cutoff.tzinfo is None:
        cutoff = cutoff.replace(tzinfo=timezone.utc)
    return ts >= cutoff


def _parse_iso(value: str) -> datetime | None:
    for fmt in ("%Y-%m-%dT%H:%M:%S%z", "%Y-%m-%dT%H:%M:%S.%f%z", "%Y-%m-%dT%H:%M:%SZ", "%Y-%m-%dT%H:%M:%S"):
        try:
            dt = datetime.strptime(value.rstrip("Z") + "+00:00" if value.endswith("Z") else value, fmt)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
        except ValueError:
            continue
    return None


def _to_float(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None
