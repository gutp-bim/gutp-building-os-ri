"""
Parquet loader for MinIO cold storage.
Writes a batch of rows as a Parquet file partitioned by year/month.
"""

from __future__ import annotations
import io
import logging
from datetime import datetime, timezone
from typing import Any, Sequence

import pyarrow as pa
import pyarrow.parquet as pq
from minio import Minio

logger = logging.getLogger(__name__)

_SCHEMA = pa.schema([
    pa.field("time", pa.timestamp("us", tz="UTC")),
    pa.field("point_id", pa.string()),
    pa.field("building", pa.string()),
    pa.field("device_id", pa.string()),
    pa.field("name", pa.string()),
    pa.field("value", pa.float64()),
    pa.field("data", pa.string()),   # JSON string
    pa.field("id", pa.string()),
])


class MinioLoader:
    def __init__(
        self,
        endpoint: str,
        access_key: str,
        secret_key: str,
        bucket: str,
        prefix: str = "telemetry",
        secure: bool = False,
    ) -> None:
        self._client = Minio(endpoint, access_key=access_key, secret_key=secret_key, secure=secure)
        self._bucket = bucket
        self._prefix = prefix

    def ensure_bucket(self) -> None:
        if not self._client.bucket_exists(self._bucket):
            self._client.make_bucket(self._bucket)

    def write_batch(self, rows: Sequence[dict[str, Any]], batch_id: str) -> str:
        """Write rows to Parquet in MinIO; returns the object key."""
        if not rows:
            raise ValueError("rows must not be empty")

        import json as _json

        table = pa.table(
            {
                "time": [_to_pa_ts(r.get("time")) for r in rows],
                "point_id": [r.get("point_id") or "" for r in rows],
                "building": [r.get("building") for r in rows],
                "device_id": [r.get("device_id") for r in rows],
                "name": [r.get("name") for r in rows],
                "value": [r.get("value") for r in rows],
                "data": [
                    _json.dumps(r["data"]) if isinstance(r.get("data"), dict) else r.get("data")
                    for r in rows
                ],
                "id": [r.get("id") for r in rows],
            },
            schema=_SCHEMA,
        )

        buf = io.BytesIO()
        pq.write_table(table, buf, compression="snappy")
        buf.seek(0)
        size = buf.getbuffer().nbytes

        # Partition by first row's year/month
        ts: datetime = rows[0]["time"]
        if ts.tzinfo is None:
            ts = ts.replace(tzinfo=timezone.utc)
        key = f"{self._prefix}/year={ts.year}/month={ts.month:02d}/{batch_id}.parquet"

        self._client.put_object(
            self._bucket,
            key,
            buf,
            length=size,
            content_type="application/octet-stream",
        )
        logger.debug("Wrote %d rows to MinIO: %s/%s", len(rows), self._bucket, key)
        return key


def _to_pa_ts(value: Any) -> int | None:
    """Convert datetime to microseconds since epoch for PyArrow timestamp."""
    if value is None:
        return None
    if isinstance(value, datetime):
        if value.tzinfo is None:
            value = value.replace(tzinfo=timezone.utc)
        epoch = datetime(1970, 1, 1, tzinfo=timezone.utc)
        return int((value - epoch).total_seconds() * 1_000_000)
    return None
