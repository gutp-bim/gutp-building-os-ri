"""
Ported logic from azure/fabric/lakehouse/notebook/
Replaces PySpark/Delta Table operations with DuckDB + Parquet on MinIO.

Original notebook: insert-to-delta-table.py
Logic: Read JSON files from cold-data-files/, strip _ columns, serialize 'data' as JSON,
       append/overwrite to Delta Table sensordata.

DuckDB equivalent: reads Parquet files directly from MinIO and creates
a unified view with the same column semantics.
"""

from __future__ import annotations

import duckdb


def build_sensordata_view(con: duckdb.DuckDBPyConnection, parquet_glob: str) -> None:
    """
    Create a `sensordata` view matching the original Delta Table schema.
    Strips internal system columns (starting with _) and serialises 'data' as JSON string.

    Args:
        con: DuckDB connection (must have S3/MinIO configured if using s3:// paths)
        parquet_glob: glob path to Parquet files, e.g. 's3://bucket/telemetry/**/*.parquet'
    """
    con.execute(f"""
        CREATE OR REPLACE VIEW sensordata AS
        SELECT
            time,
            point_id,
            building,
            device_id,
            name,
            value,
            id,
            CASE
                WHEN data IS NOT NULL THEN json(data)
                ELSE NULL
            END AS data
        FROM read_parquet('{parquet_glob}', hive_partitioning=true)
    """)


def append_parquet_to_sensordata(
    con: duckdb.DuckDBPyConnection,
    source_parquet: str,
    target_table: str = "sensordata_materialized",
) -> int:
    """
    Append rows from a source Parquet file to a materialised DuckDB table.
    Equivalent to: df.write.format("delta").mode("append").save(delta_path)

    Returns number of rows inserted.
    """
    result = con.execute(f"""
        INSERT INTO {target_table}
        SELECT time, point_id, building, device_id, name, value, id,
               CASE WHEN data IS NOT NULL THEN json(data) ELSE NULL END AS data
        FROM read_parquet('{source_parquet}')
    """)
    return result.fetchone()[0] if result else 0


def create_sensordata_table(con: duckdb.DuckDBPyConnection) -> None:
    """Create the materialised sensordata table (run once)."""
    con.execute("""
        CREATE TABLE IF NOT EXISTS sensordata_materialized (
            time        TIMESTAMPTZ,
            point_id    VARCHAR,
            building    VARCHAR,
            device_id   VARCHAR,
            name        VARCHAR,
            value       DOUBLE,
            id          VARCHAR,
            data        JSON
        )
    """)


# ── Ported analytical queries from Fabric notebooks ──────────────────────────

BUILDING_DAILY_ENERGY_SQL = """
-- Ported from Fabric notebook: daily energy summary per building
-- Original: Spark SQL GROUP BY building, strftime(time, '%Y-%m-%d')
SELECT
    strftime(time, '%Y-%m-%d') AS date,
    building,
    point_id,
    AVG(value)      AS avg_value,
    SUM(value)      AS sum_value,
    MIN(value)      AS min_value,
    MAX(value)      AS max_value,
    COUNT(*)        AS sample_count
FROM sensordata
WHERE building = $building
  AND time >= $from_date::TIMESTAMPTZ
  AND time <  $to_date::TIMESTAMPTZ
GROUP BY 1, 2, 3
ORDER BY 1, 2, 3
"""

MERGE_JSON_FILES_SQL = """
-- Ported from merge-json-files.py: deduplicate overlapping JSON/Parquet exports
-- Original: Spark read JSON, drop duplicates by id, write back as JSON
SELECT DISTINCT ON (id) *
FROM read_parquet($parquet_glob)
ORDER BY id, time DESC
"""
