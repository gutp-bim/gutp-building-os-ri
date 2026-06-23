"""Tests for DuckDB analytics query layer using local Parquet files."""

from __future__ import annotations

import os
import tempfile
from datetime import datetime, timezone

import duckdb
import pyarrow as pa
import pyarrow.parquet as pq
import pytest

# Import functions under test — adjust path so pytest finds the module
import sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from duckdb_queries.query import (
    DAILY_SUMMARY_SQL,
    HOURLY_SUMMARY_SQL,
    LATEST_PER_POINT_SQL,
    build_connection,
    run_query,
)


def _write_test_parquet(tmp_dir: str) -> str:
    """Write minimal test Parquet file and return the path glob."""
    table = pa.table({
        "time": pa.array([
            datetime(2024, 3, 1, 8, 0, tzinfo=timezone.utc),
            datetime(2024, 3, 1, 9, 0, tzinfo=timezone.utc),
            datetime(2024, 3, 2, 8, 0, tzinfo=timezone.utc),
        ], type=pa.timestamp("us", tz="UTC")),
        "point_id": ["PT001", "PT001", "PT002"],
        "building": ["ENG2", "ENG2", "ENG2"],
        "device_id": ["DEV1", "DEV1", "DEV2"],
        "name": ["temp", "temp", "humidity"],
        "value": [21.5, 22.0, 55.0],
        "data": [None, None, None],
        "id": ["d1", "d2", "d3"],
    })
    out = os.path.join(tmp_dir, "test.parquet")
    pq.write_table(table, out)
    return out


def _make_in_memory_db(parquet_path: str) -> duckdb.DuckDBPyConnection:
    """Create an in-memory DuckDB with a telemetry view over a local parquet file."""
    con = duckdb.connect(":memory:")
    con.execute(f"CREATE OR REPLACE VIEW telemetry AS SELECT * FROM read_parquet('{parquet_path}')")
    return con


class TestRunQuery:
    def test_run_query_returns_rows(self, tmp_path):
        path = _write_test_parquet(str(tmp_path))
        con = _make_in_memory_db(path)
        result = run_query(con, "SELECT COUNT(*) AS cnt FROM telemetry")
        df = result.df()
        assert df["cnt"][0] == 3

    def test_run_query_filters_by_building(self, tmp_path):
        path = _write_test_parquet(str(tmp_path))
        con = _make_in_memory_db(path)
        result = run_query(con, "SELECT * FROM telemetry WHERE building = 'ENG2'")
        assert len(result.df()) == 3

    def test_run_query_no_results_for_unknown_building(self, tmp_path):
        path = _write_test_parquet(str(tmp_path))
        con = _make_in_memory_db(path)
        result = run_query(con, "SELECT * FROM telemetry WHERE building = 'UNKNOWN'")
        assert len(result.df()) == 0


class TestCannedQueries:
    def test_daily_summary_groups_by_date(self, tmp_path):
        path = _write_test_parquet(str(tmp_path))
        con = _make_in_memory_db(path)
        result = con.execute(DAILY_SUMMARY_SQL, {
            "building": "ENG2",
            "from_ts": "2024-03-01",
            "to_ts": "2024-03-31",
        })
        df = result.df()
        assert len(df) >= 2  # 2024-03-01 PT001, 2024-03-02 PT002

    def test_daily_summary_computes_avg(self, tmp_path):
        path = _write_test_parquet(str(tmp_path))
        con = _make_in_memory_db(path)
        result = con.execute(DAILY_SUMMARY_SQL, {
            "building": "ENG2",
            "from_ts": "2024-03-01",
            "to_ts": "2024-03-02",
        })
        df = result.df()
        pt001_row = df[df["point_id"] == "PT001"]
        assert len(pt001_row) == 1
        assert abs(pt001_row["avg_value"].iloc[0] - 21.75) < 0.01

    def test_hourly_summary_groups_by_hour(self, tmp_path):
        path = _write_test_parquet(str(tmp_path))
        con = _make_in_memory_db(path)
        result = con.execute(HOURLY_SUMMARY_SQL, {
            "building": "ENG2",
            "from_ts": "2024-03-01",
            "to_ts": "2024-03-31",
        })
        df = result.df()
        assert len(df) >= 1

    def test_latest_per_point_returns_most_recent(self, tmp_path):
        path = _write_test_parquet(str(tmp_path))
        con = _make_in_memory_db(path)
        result = con.execute(LATEST_PER_POINT_SQL, {"building": "ENG2"})
        df = result.df()
        pt001 = df[df["point_id"] == "PT001"]
        # Most recent PT001 reading is value=22.0 at 09:00
        assert abs(pt001["value"].iloc[0] - 22.0) < 0.01
