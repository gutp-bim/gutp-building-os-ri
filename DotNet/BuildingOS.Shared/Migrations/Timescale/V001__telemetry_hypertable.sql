-- V001: telemetry hypertable + compression + retention policies
-- Applies to: TimescaleDB 2.x on PostgreSQL 15/16

-- ============================================================
-- 1. TimescaleDB extension
-- ============================================================
CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;

-- ============================================================
-- 2. Core telemetry hypertable
--    Maps 1-to-1 with CosmosDB / Microsoft Fabric sensordata
-- ============================================================
CREATE TABLE IF NOT EXISTS telemetry (
    time        TIMESTAMPTZ     NOT NULL,
    point_id    TEXT            NOT NULL,
    building    TEXT,
    device_id   TEXT,
    name        TEXT,
    value       DOUBLE PRECISION,
    data        JSONB,          -- raw device payload (replaces CosmosDB "data" JSON string)
    id          TEXT            -- original CosmosDB document id (kept for traceability)
);

-- Partition by time (1-day chunks); point_id is the secondary dimension
SELECT create_hypertable(
    'telemetry',
    'time',
    chunk_time_interval => INTERVAL '1 day',
    if_not_exists => TRUE
);

-- ============================================================
-- 3. Indexes
-- ============================================================
CREATE INDEX IF NOT EXISTS idx_telemetry_point_time
    ON telemetry (point_id, time DESC);

CREATE INDEX IF NOT EXISTS idx_telemetry_building_time
    ON telemetry (building, time DESC);

-- ============================================================
-- 4. Compression policy
--    Compress chunks older than 7 days (~10x size reduction)
--    Order by (point_id, time) for optimal segment layout
-- ============================================================
ALTER TABLE telemetry
    SET (
        timescaledb.compress,
        timescaledb.compress_segmentby = 'point_id',
        timescaledb.compress_orderby = 'time DESC'
    );

SELECT add_compression_policy(
    'telemetry',
    compress_after => INTERVAL '7 days',
    if_not_exists => TRUE
);

-- ============================================================
-- 5. Retention policy
--    Drop chunks older than 3 months AFTER Parquet export
--    (enforced at application level — see ColdExportJob)
--    Registered here as a safety net with a 120-day limit
-- ============================================================
SELECT add_retention_policy(
    'telemetry',
    drop_after => INTERVAL '120 days',
    if_not_exists => TRUE
);

-- ============================================================
-- 6. Continuous aggregate (hourly rollup for dashboard queries)
-- ============================================================
CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry_hourly
    WITH (timescaledb.continuous) AS
    SELECT
        time_bucket('1 hour', time) AS bucket,
        point_id,
        building,
        AVG(value)  AS avg_value,
        MIN(value)  AS min_value,
        MAX(value)  AS max_value,
        COUNT(*)    AS sample_count
    FROM telemetry
    GROUP BY bucket, point_id, building
    WITH NO DATA;

SELECT add_continuous_aggregate_policy(
    'telemetry_hourly',
    start_offset => INTERVAL '3 days',
    end_offset   => INTERVAL '1 hour',
    schedule_interval => INTERVAL '1 hour',
    if_not_exists => TRUE
);

-- ============================================================
-- 7. Cold export tracking table
--    Records which monthly Parquet chunks have been exported
--    to MinIO and verified before drop_chunks() is called
-- ============================================================
CREATE TABLE IF NOT EXISTS cold_export_log (
    id              SERIAL PRIMARY KEY,
    chunk_start     TIMESTAMPTZ NOT NULL,
    chunk_end       TIMESTAMPTZ NOT NULL,
    parquet_path    TEXT        NOT NULL,   -- MinIO object key
    exported_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    verified        BOOLEAN     NOT NULL DEFAULT FALSE,
    verified_at     TIMESTAMPTZ,
    rows_exported   BIGINT,
    bytes_written   BIGINT
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_cold_export_chunk
    ON cold_export_log (chunk_start, chunk_end);
