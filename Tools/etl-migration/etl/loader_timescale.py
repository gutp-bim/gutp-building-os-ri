"""
Bulk loader for TimescaleDB using asyncpg COPY protocol.
Writes rows to the `telemetry` hypertable.
"""

from __future__ import annotations
import json
import logging
from datetime import datetime
from typing import Any, Sequence

import asyncpg

logger = logging.getLogger(__name__)

_COPY_SQL = """
COPY telemetry (time, point_id, building, device_id, name, value, data, id)
FROM STDIN
"""

_INSERT_SQL = """
INSERT INTO telemetry (time, point_id, building, device_id, name, value, data, id)
VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8)
ON CONFLICT DO NOTHING
"""


class TimescaleLoader:
    def __init__(self, dsn: str) -> None:
        self._dsn = dsn
        self._conn: asyncpg.Connection | None = None

    async def connect(self) -> None:
        self._conn = await asyncpg.connect(self._dsn)
        await self._conn.set_type_codec(
            "jsonb",
            encoder=json.dumps,
            decoder=json.loads,
            schema="pg_catalog",
        )

    async def close(self) -> None:
        if self._conn is not None:
            await self._conn.close()
            self._conn = None

    async def __aenter__(self) -> "TimescaleLoader":
        await self.connect()
        return self

    async def __aexit__(self, *_: Any) -> None:
        await self.close()

    async def insert_batch(self, rows: Sequence[dict[str, Any]]) -> int:
        """Insert rows using executemany; returns count of rows attempted."""
        if not rows:
            return 0
        assert self._conn is not None, "call connect() first"

        records = [_row_to_tuple(r) for r in rows]
        await self._conn.executemany(_INSERT_SQL, records)
        logger.debug("Inserted batch of %d rows into TimescaleDB", len(records))
        return len(records)


def _row_to_tuple(row: dict[str, Any]) -> tuple:
    data = row.get("data")
    if data is not None and not isinstance(data, str):
        data = json.dumps(data)
    return (
        row["time"],
        row.get("point_id") or "",
        row.get("building"),
        row.get("device_id"),
        row.get("name"),
        row.get("value"),
        data,
        row.get("id"),
    )
