-- V002: point_control_audit table
-- Replaces CosmosDB point control container for OSS stack

CREATE TABLE IF NOT EXISTS point_control_audit (
    id           UUID        PRIMARY KEY,
    point_id     TEXT        NOT NULL,
    request      JSONB       NOT NULL,
    result       JSONB,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_pca_point_created
    ON point_control_audit (point_id, created_at DESC);

-- Retention: keep records for 1 year
-- Older records are archived to cold storage before deletion
-- Application-layer enforcement: ControlAuditArchiveJob (monthly)
COMMENT ON TABLE point_control_audit IS
    'Audit log for device control commands. Retention: 1 year. Older records archived to MinIO.';
