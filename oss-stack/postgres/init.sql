-- TimescaleDB warm-tier initialization — OPTIONAL.
--
-- Since #216/#234 the OSS default image is postgres:16 (parquet warm tier), which does NOT ship
-- timescaledb. Running the timescaledb DDL unconditionally made the default `make local-up-oss`
-- stack fail at init (extension not available → container exit → whole stack down). Guard the entire
-- TimescaleDB setup on the extension being installable, so:
--   * postgres:16 (parquet default) → this script is a no-op; users/groups/audit come from EF migrations.
--   * a timescaledb image (WARM_STORE=timescale) → the warm-tier telemetry objects are created.
SELECT EXISTS (
    SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb'
) AS has_timescaledb \gset

\if :has_timescaledb

CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;

-- Telemetry hypertable (warm tier)
CREATE TABLE IF NOT EXISTS telemetry (
    time        TIMESTAMPTZ       NOT NULL,
    point_id    TEXT              NOT NULL,
    building    TEXT,
    device_id   TEXT              NOT NULL DEFAULT '',
    name        TEXT,
    value       DOUBLE PRECISION,
    data        JSONB,
    id          TEXT
);

SELECT create_hypertable('telemetry', 'time', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS idx_telemetry_point_time
    ON telemetry (point_id, time DESC);

CREATE INDEX IF NOT EXISTS idx_telemetry_data_run_id
    ON telemetry USING gin (data jsonb_path_ops)
    WHERE data IS NOT NULL;

-- Continuous aggregates (hourly / daily) for granularity-aware API queries
CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry_hourly
WITH (timescaledb.continuous) AS
    SELECT
        time_bucket('1 hour', time) AS time,
        point_id,
        building,
        device_id,
        name,
        AVG(value) AS value
    FROM telemetry
    GROUP BY 1, 2, 3, 4, 5
WITH NO DATA;

-- start_offset must cover at least two 1-hour buckets beyond end_offset, otherwise
-- TimescaleDB rejects the policy ("refresh window too small").
SELECT add_continuous_aggregate_policy('telemetry_hourly',
    start_offset => INTERVAL '3 hours',
    end_offset   => INTERVAL '1 hour',
    schedule_interval => INTERVAL '10 minutes',
    if_not_exists => TRUE);

CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry_daily
WITH (timescaledb.continuous) AS
    SELECT
        time_bucket('1 day', time) AS time,
        point_id,
        building,
        device_id,
        name,
        AVG(value) AS value
    FROM telemetry
    GROUP BY 1, 2, 3, 4, 5
WITH NO DATA;

-- start_offset must cover at least two 1-day buckets beyond end_offset.
SELECT add_continuous_aggregate_policy('telemetry_daily',
    start_offset => INTERVAL '3 days',
    end_offset   => INTERVAL '1 day',
    schedule_interval => INTERVAL '1 hour',
    if_not_exists => TRUE);

\else

\echo 'timescaledb not available (postgres:16 / parquet warm tier) — skipping TimescaleDB warm-tier init'

\endif
