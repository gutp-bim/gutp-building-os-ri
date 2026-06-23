#!/usr/bin/env bash
# TimescaleDB compression ratio measurement script.
# Requires compressed chunks to exist (run after compress_after interval = 7 days has elapsed).
set -euo pipefail

docker exec building-os.postgres psql -U buildingos -d buildingos <<'SQL'
-- Per-chunk compression stats
SELECT
    chunk_name,
    before_compression_total_bytes / 1024.0 / 1024.0 AS before_mb,
    after_compression_total_bytes  / 1024.0 / 1024.0 AS after_mb,
    ROUND(
        before_compression_total_bytes::numeric
        / NULLIF(after_compression_total_bytes, 0), 2
    ) AS compression_ratio
FROM chunk_compression_stats('telemetry')
ORDER BY chunk_name;

-- Overall summary
SELECT
    pg_size_pretty(before_compression_total_bytes) AS before_total,
    pg_size_pretty(after_compression_total_bytes)  AS after_total,
    ROUND(
        before_compression_total_bytes::numeric
        / NULLIF(after_compression_total_bytes, 0), 2
    ) AS total_compression_ratio
FROM (
    SELECT
        SUM(before_compression_total_bytes) AS before_compression_total_bytes,
        SUM(after_compression_total_bytes)  AS after_compression_total_bytes
    FROM chunk_compression_stats('telemetry')
) s;
SQL
