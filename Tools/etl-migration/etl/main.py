"""
ETL orchestrator: CosmosDB → TimescaleDB (warm) / MinIO Parquet (cold).

Usage:
    python -m etl.main [--dry-run] [--batch-size N] [--checkpoint FILE]

Environment variables:
    COSMOS_CONNECTION_STRING   CosmosDB connection string
    COSMOS_DATABASE_NAME       CosmosDB database name
    COSMOS_CONTAINER_NAME      CosmosDB container name
    TIMESCALE_DSN              asyncpg-compatible DSN (warm data, >=cutoff)
    MINIO_ENDPOINT             MinIO endpoint (e.g. localhost:9000)
    MINIO_ACCESS_KEY           MinIO access key
    MINIO_SECRET_KEY           MinIO secret key
    MINIO_BUCKET               MinIO bucket name (default: building-os-cold)
    WARM_CUTOFF_DAYS           Days back from now for warm/cold split (default: 90)
    BATCH_SIZE                 Rows per commit (default: 500)
    CHECKPOINT_FILE            Path to checkpoint JSON (default: checkpoint.json)
    DRY_RUN                    Set to "1" to skip writes (default: 0)
"""

from __future__ import annotations

import argparse
import asyncio
import logging
import os
import sys
import uuid
from datetime import datetime, timedelta, timezone
from typing import Any

from dotenv import load_dotenv
from prometheus_client import CollectorRegistry, Counter, Gauge, push_to_gateway
from tqdm import tqdm

from etl.checkpoint import Checkpoint
from etl.loader_minio import MinioLoader
from etl.loader_timescale import TimescaleLoader
from etl.transform import cosmos_doc_to_row, is_warm

load_dotenv()

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
logger = logging.getLogger("etl.main")


def _env(key: str, default: str | None = None) -> str:
    val = os.environ.get(key, default)
    if val is None:
        raise EnvironmentError(f"Required environment variable not set: {key}")
    return val


class EtlConfig:
    def __init__(self) -> None:
        self.cosmos_connection_string = _env("COSMOS_CONNECTION_STRING")
        self.cosmos_database = _env("COSMOS_DATABASE_NAME")
        self.cosmos_container = _env("COSMOS_CONTAINER_NAME")
        self.timescale_dsn = _env("TIMESCALE_DSN")
        self.minio_endpoint = _env("MINIO_ENDPOINT")
        self.minio_access_key = _env("MINIO_ACCESS_KEY")
        self.minio_secret_key = _env("MINIO_SECRET_KEY")
        self.minio_bucket = os.environ.get("MINIO_BUCKET", "building-os-cold")
        warm_days = int(os.environ.get("WARM_CUTOFF_DAYS", "90"))
        self.warm_cutoff: datetime = datetime.now(tz=timezone.utc) - timedelta(days=warm_days)
        self.batch_size = int(os.environ.get("BATCH_SIZE", "500"))
        self.checkpoint_file = os.environ.get("CHECKPOINT_FILE", "checkpoint.json")
        self.dry_run = os.environ.get("DRY_RUN", "0") == "1"
        self.pushgateway_url: str | None = os.environ.get("PROMETHEUS_PUSHGATEWAY_URL")


async def _fetch_all_docs(config: EtlConfig, checkpoint: Checkpoint):
    """Yield CosmosDB documents page-by-page, resuming from checkpoint."""
    from azure.cosmos.aio import CosmosClient

    continuation_token = checkpoint.get_continuation_token()

    async with CosmosClient.from_connection_string(config.cosmos_connection_string) as client:
        container = client.get_database_client(config.cosmos_database).get_container_client(
            config.cosmos_container
        )

        query = "SELECT * FROM c ORDER BY c._ts ASC"
        kw: dict[str, Any] = {"max_item_count": config.batch_size}
        if continuation_token:
            logger.info("Resuming from checkpoint token (first 30 chars): %s...", continuation_token[:30])
            kw["continuation"] = continuation_token

        async for page in container.query_items(query=query, **kw).by_page(**kw):
            items = [item async for item in page]
            yield items, page.continuation_token


async def run(config: EtlConfig) -> int:
    """Run the full ETL. Returns number of rows written."""
    checkpoint = Checkpoint(config.checkpoint_file)
    registry = CollectorRegistry()

    rows_total = Counter("etl_rows_total", "Rows processed", ["dest"], registry=registry)
    rows_skipped = Counter("etl_rows_skipped", "Rows skipped (no timestamp)", registry=registry)
    batches_done = Counter("etl_batches_completed", "Batches committed", registry=registry)
    last_ts_gauge = Gauge("etl_last_ts_epoch", "Epoch of last processed timestamp", registry=registry)

    warm_batch: list[dict[str, Any]] = []
    cold_batch: list[dict[str, Any]] = []
    total_written = 0
    last_ts_str: str | None = None

    async with TimescaleLoader(config.timescale_dsn) as ts_loader:
        minio_loader = MinioLoader(
            config.minio_endpoint,
            config.minio_access_key,
            config.minio_secret_key,
            config.minio_bucket,
        )
        if not config.dry_run:
            minio_loader.ensure_bucket()

        last_token: str | None = None

        async for page_docs, token in _fetch_all_docs(config, checkpoint):
            last_token = token

            for doc in page_docs:
                row = cosmos_doc_to_row(doc)
                if row is None:
                    rows_skipped.inc()
                    continue

                ts: datetime = row["time"]
                last_ts_str = ts.isoformat()
                last_ts_gauge.set(ts.timestamp())

                if is_warm(ts, config.warm_cutoff):
                    warm_batch.append(row)
                else:
                    cold_batch.append(row)

            # Flush warm batch
            if len(warm_batch) >= config.batch_size:
                if not config.dry_run:
                    written = await ts_loader.insert_batch(warm_batch)
                    rows_total.labels(dest="timescale").inc(written)
                    total_written += written
                else:
                    total_written += len(warm_batch)
                warm_batch = []
                batches_done.inc()

            # Flush cold batch
            if len(cold_batch) >= config.batch_size:
                if not config.dry_run:
                    batch_id = str(uuid.uuid4())
                    minio_loader.write_batch(cold_batch, batch_id)
                    rows_total.labels(dest="minio").inc(len(cold_batch))
                total_written += len(cold_batch)
                cold_batch = []
                batches_done.inc()

            checkpoint.update(last_token, last_ts_str, total_written)

        # Flush remaining
        if warm_batch:
            if not config.dry_run:
                written = await ts_loader.insert_batch(warm_batch)
                rows_total.labels(dest="timescale").inc(written)
                total_written += written
            else:
                total_written += len(warm_batch)

        if cold_batch:
            if not config.dry_run:
                batch_id = str(uuid.uuid4())
                minio_loader.write_batch(cold_batch, batch_id)
                rows_total.labels(dest="minio").inc(len(cold_batch))
            total_written += len(cold_batch)

    checkpoint.update(None, last_ts_str, total_written)
    logger.info("ETL complete. Total rows: %d", total_written)

    if config.pushgateway_url:
        try:
            push_to_gateway(config.pushgateway_url, job="etl_migration", registry=registry)
        except Exception as exc:
            logger.warning("Prometheus push failed: %s", exc)

    return total_written


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="CosmosDB → TimescaleDB/MinIO ETL")
    parser.add_argument("--dry-run", action="store_true", help="Skip writes, count only")
    parser.add_argument("--batch-size", type=int, default=None)
    parser.add_argument("--checkpoint", default=None)
    args = parser.parse_args(argv)

    if args.dry_run:
        os.environ["DRY_RUN"] = "1"
    if args.batch_size:
        os.environ["BATCH_SIZE"] = str(args.batch_size)
    if args.checkpoint:
        os.environ["CHECKPOINT_FILE"] = args.checkpoint

    try:
        config = EtlConfig()
    except EnvironmentError as exc:
        logger.error("%s", exc)
        return 1

    total = asyncio.run(run(config))
    logger.info("Migration finished. Rows written: %d", total)
    return 0


if __name__ == "__main__":
    sys.exit(main())
