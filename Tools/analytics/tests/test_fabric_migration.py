"""Tests for Fabric notebook migration to DuckDB."""

import os
import sys
from datetime import datetime, timezone

import duckdb
import pyarrow as pa
import pyarrow.parquet as pq
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from duckdb_queries.fabric_migration import (
    BUILDING_DAILY_ENERGY_SQL,
    MERGE_JSON_FILES_SQL,
    append_parquet_to_sensordata,
    build_sensordata_view,
    create_sensordata_table,
)


def _sample_table() -> pa.Table:
    return pa.table({
        "time": pa.array([
            datetime(2024, 4, 1, 8, tzinfo=timezone.utc),
            datetime(2024, 4, 1, 9, tzinfo=timezone.utc),
            datetime(2024, 4, 1, 9, tzinfo=timezone.utc),  # duplicate
        ], type=pa.timestamp("us", tz="UTC")),
        "point_id": ["P1", "P1", "P1"],
        "building": ["ENG2", "ENG2", "ENG2"],
        "device_id": ["D1", "D1", "D1"],
        "name": ["temp", "temp", "temp"],
        "value": [20.0, 21.0, 21.0],
        "data": [None, None, None],
        "id": ["id1", "id2", "id2"],  # id2 appears twice
    })


class TestBuildSensordataView:
    def test_view_created(self, tmp_path):
        parquet_path = str(tmp_path / "data.parquet")
        pq.write_table(_sample_table(), parquet_path)
        con = duckdb.connect(":memory:")
        build_sensordata_view(con, parquet_path)
        result = con.execute("SELECT COUNT(*) FROM sensordata").fetchone()
        assert result[0] == 3

    def test_data_column_as_json(self, tmp_path):
        t = pa.table({
            "time": pa.array([datetime(2024, 1, 1, tzinfo=timezone.utc)], type=pa.timestamp("us", tz="UTC")),
            "point_id": ["P1"], "building": ["B1"], "device_id": ["D1"],
            "name": ["x"], "value": [1.0], "data": ['{"k": 1}'], "id": ["id1"],
        })
        path = str(tmp_path / "t.parquet")
        pq.write_table(t, path)
        con = duckdb.connect(":memory:")
        build_sensordata_view(con, path)
        row = con.execute("SELECT data FROM sensordata").fetchone()
        assert row[0] is not None


class TestCreateAndAppend:
    def test_append_to_table(self, tmp_path):
        parquet_path = str(tmp_path / "data.parquet")
        pq.write_table(_sample_table(), parquet_path)
        con = duckdb.connect(":memory:")
        create_sensordata_table(con)
        append_parquet_to_sensordata(con, parquet_path)
        count = con.execute("SELECT COUNT(*) FROM sensordata_materialized").fetchone()[0]
        assert count == 3

    def test_create_table_idempotent(self, tmp_path):
        con = duckdb.connect(":memory:")
        create_sensordata_table(con)
        create_sensordata_table(con)  # second call should not raise
        count = con.execute("SELECT COUNT(*) FROM sensordata_materialized").fetchone()[0]
        assert count == 0


class TestBuildingDailyEnergy:
    def test_groups_correctly(self, tmp_path):
        parquet_path = str(tmp_path / "data.parquet")
        pq.write_table(_sample_table(), parquet_path)
        con = duckdb.connect(":memory:")
        build_sensordata_view(con, parquet_path)
        result = con.execute(BUILDING_DAILY_ENERGY_SQL, {
            "building": "ENG2",
            "from_date": "2024-04-01",
            "to_date": "2024-04-02",
        })
        df = result.df()
        assert len(df) == 1  # one date, one point
        assert abs(df["avg_value"].iloc[0] - (20.0 + 21.0 + 21.0) / 3) < 0.01
