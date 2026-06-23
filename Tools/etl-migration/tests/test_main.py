"""Tests for ETL orchestrator main.py using mocks (no real DB connections)."""

from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from etl.main import EtlConfig, run
from etl.checkpoint import Checkpoint


def _make_config(tmp_path: Path, dry_run: bool = True) -> EtlConfig:
    """Build a minimal EtlConfig without real env vars by setting env directly."""
    import os

    env = {
        "COSMOS_CONNECTION_STRING": "AccountEndpoint=https://fake.documents.azure.com:443/;AccountKey=abc==",
        "COSMOS_DATABASE_NAME": "buildingos",
        "COSMOS_CONTAINER_NAME": "telemetry",
        "TIMESCALE_DSN": "postgresql://user:pass@localhost:5432/db",
        "MINIO_ENDPOINT": "localhost:9000",
        "MINIO_ACCESS_KEY": "minio",
        "MINIO_SECRET_KEY": "minio123",
        "MINIO_BUCKET": "test-bucket",
        "WARM_CUTOFF_DAYS": "90",
        "BATCH_SIZE": "2",
        "CHECKPOINT_FILE": str(tmp_path / "checkpoint.json"),
        "DRY_RUN": "1" if dry_run else "0",
    }
    for k, v in env.items():
        os.environ[k] = v

    try:
        return EtlConfig()
    finally:
        for k in env:
            os.environ.pop(k, None)


def _make_doc(ts: datetime, point_id: str = "p1", value: float = 20.0) -> dict[str, Any]:
    return {
        "id": f"doc-{point_id}",
        "datetime": ts.isoformat(),
        "point_id": point_id,
        "building": "ENG2",
        "device_id": "dev1",
        "name": "temp",
        "value": value,
    }


@pytest.mark.asyncio
async def test_run_dry_run_counts_rows(tmp_path):
    """Dry-run should count rows without calling DB insert."""
    now = datetime.now(tz=timezone.utc)
    warm_ts = now - timedelta(days=30)
    cold_ts = now - timedelta(days=120)

    docs = [_make_doc(warm_ts, "p1"), _make_doc(cold_ts, "p2")]

    async def fake_fetch(config, checkpoint):
        yield docs, None

    config = _make_config(tmp_path, dry_run=True)

    with patch("etl.main._fetch_all_docs", fake_fetch), \
         patch("etl.main.TimescaleLoader") as mock_ts_class, \
         patch("etl.main.MinioLoader") as mock_minio_class:

        mock_ts = AsyncMock()
        mock_ts.insert_batch = AsyncMock(return_value=0)
        mock_ts_class.return_value.__aenter__ = AsyncMock(return_value=mock_ts)
        mock_ts_class.return_value.__aexit__ = AsyncMock(return_value=None)

        mock_minio = MagicMock()
        mock_minio.ensure_bucket = MagicMock()
        mock_minio_class.return_value = mock_minio

        total = await run(config)

    assert total == 2
    mock_ts.insert_batch.assert_not_called()
    mock_minio.write_batch.assert_not_called()


@pytest.mark.asyncio
async def test_run_routes_warm_to_timescale(tmp_path):
    """Warm rows (within cutoff) must be sent to TimescaleDB."""
    now = datetime.now(tz=timezone.utc)
    warm_ts = now - timedelta(days=10)
    doc = _make_doc(warm_ts, "p_warm")

    async def fake_fetch(config, checkpoint):
        # Yield 3 docs to trigger batch flush (batch_size=2 → flush at 2)
        yield [doc, doc], "tok1"
        yield [doc], None

    config = _make_config(tmp_path, dry_run=False)

    with patch("etl.main._fetch_all_docs", fake_fetch), \
         patch("etl.main.TimescaleLoader") as mock_ts_class, \
         patch("etl.main.MinioLoader") as mock_minio_class:

        mock_ts = AsyncMock()
        mock_ts.insert_batch = AsyncMock(return_value=2)
        mock_ts_class.return_value.__aenter__ = AsyncMock(return_value=mock_ts)
        mock_ts_class.return_value.__aexit__ = AsyncMock(return_value=None)

        mock_minio = MagicMock()
        mock_minio.ensure_bucket = MagicMock()
        mock_minio.write_batch = MagicMock(return_value="key")
        mock_minio_class.return_value = mock_minio

        await run(config)

    mock_ts.insert_batch.assert_called()
    mock_minio.write_batch.assert_not_called()


@pytest.mark.asyncio
async def test_run_routes_cold_to_minio(tmp_path):
    """Cold rows (older than cutoff) must be sent to MinIO as Parquet."""
    now = datetime.now(tz=timezone.utc)
    cold_ts = now - timedelta(days=200)
    doc = _make_doc(cold_ts, "p_cold")

    async def fake_fetch(config, checkpoint):
        yield [doc, doc], "tok1"
        yield [doc], None

    config = _make_config(tmp_path, dry_run=False)

    with patch("etl.main._fetch_all_docs", fake_fetch), \
         patch("etl.main.TimescaleLoader") as mock_ts_class, \
         patch("etl.main.MinioLoader") as mock_minio_class:

        mock_ts = AsyncMock()
        mock_ts.insert_batch = AsyncMock(return_value=0)
        mock_ts_class.return_value.__aenter__ = AsyncMock(return_value=mock_ts)
        mock_ts_class.return_value.__aexit__ = AsyncMock(return_value=None)

        mock_minio = MagicMock()
        mock_minio.ensure_bucket = MagicMock()
        mock_minio.write_batch = MagicMock(return_value="key/2023/01/batch.parquet")
        mock_minio_class.return_value = mock_minio

        await run(config)

    mock_minio.write_batch.assert_called()
    mock_ts.insert_batch.assert_not_called()


@pytest.mark.asyncio
async def test_run_skips_docs_without_timestamp(tmp_path):
    """Documents without a timestamp field must be skipped, not crash."""
    bad_doc = {"id": "no-time", "point_id": "p1", "value": 10}
    good_doc = _make_doc(datetime.now(tz=timezone.utc) - timedelta(days=10))

    async def fake_fetch(config, checkpoint):
        yield [bad_doc, good_doc], None

    config = _make_config(tmp_path, dry_run=True)

    with patch("etl.main._fetch_all_docs", fake_fetch), \
         patch("etl.main.TimescaleLoader") as mock_ts_class, \
         patch("etl.main.MinioLoader") as mock_minio_class:

        mock_ts = AsyncMock()
        mock_ts.insert_batch = AsyncMock(return_value=0)
        mock_ts_class.return_value.__aenter__ = AsyncMock(return_value=mock_ts)
        mock_ts_class.return_value.__aexit__ = AsyncMock(return_value=None)

        mock_minio = MagicMock()
        mock_minio.ensure_bucket = MagicMock()
        mock_minio_class.return_value = mock_minio

        total = await run(config)

    assert total == 1  # only the good doc


@pytest.mark.asyncio
async def test_run_saves_checkpoint(tmp_path):
    """Checkpoint file should be updated with last token and row count."""
    ts = datetime.now(tz=timezone.utc) - timedelta(days=10)
    doc = _make_doc(ts)

    async def fake_fetch(config, checkpoint):
        yield [doc], "final-token"

    config = _make_config(tmp_path, dry_run=True)

    with patch("etl.main._fetch_all_docs", fake_fetch), \
         patch("etl.main.TimescaleLoader") as mock_ts_class, \
         patch("etl.main.MinioLoader") as mock_minio_class:

        mock_ts = AsyncMock()
        mock_ts.insert_batch = AsyncMock(return_value=0)
        mock_ts_class.return_value.__aenter__ = AsyncMock(return_value=mock_ts)
        mock_ts_class.return_value.__aexit__ = AsyncMock(return_value=None)

        mock_minio = MagicMock()
        mock_minio.ensure_bucket = MagicMock()
        mock_minio_class.return_value = mock_minio

        await run(config)

    cp = Checkpoint(tmp_path / "checkpoint.json")
    state = cp.load()
    assert state["rows_migrated"] == 1
