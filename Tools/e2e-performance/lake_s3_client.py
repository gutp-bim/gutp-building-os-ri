"""Host-side S3-compatible client for the Parquet lake bucket (#491).

`building-os.minio` now runs RustFS (#489/#490), whose image ships no `mc` binary at all, so the
previous `docker exec <container> mc ...` pattern used by measure_bytes_per_row.py,
monitor_gateway.py and s20_retention_compaction.py fails outright. This talks the S3 API directly
against `--minio-endpoint` (host:port) instead — no `mc` install, in the container or on the host,
and no `docker exec` permission needed either. Credential resolution mirrors quality_checker.py's
DuckDB/httpfs convention (MINIO_ACCESS_KEY/MINIO_SECRET_KEY, falling back to
MINIO_ROOT_USER/MINIO_ROOT_PASSWORD, then the compose defaults) rather than reading the *container's*
env — RustFS exposes RUSTFS_ACCESS_KEY/SECRET_KEY internally, but the S3 API only cares about the
credentials it was actually started with, which is exactly what the host-side env already carries.
"""

from __future__ import annotations

import os

DEFAULT_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", os.environ.get("MINIO_ROOT_USER", "buildingos"))
DEFAULT_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", os.environ.get("MINIO_ROOT_PASSWORD", "buildingos123"))


def _client(endpoint: str, access_key: str, secret_key: str):
    import boto3
    from botocore.config import Config

    url = endpoint if "://" in endpoint else f"http://{endpoint}"
    return boto3.client(
        "s3",
        endpoint_url=url,
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4"),
    )


def list_objects_strict(endpoint: str, bucket: str, prefix: str = "",
                         access_key: str = DEFAULT_ACCESS_KEY, secret_key: str = DEFAULT_SECRET_KEY
                         ) -> list[dict]:
    """Like `list_objects`, but raises on any connection/client error instead of degrading to `[]`.
    For callers that must distinguish "queried, genuinely empty" from "could not reach the lake" —
    monitor_gateway.py's outage reporting is the reason this exists (#491 review)."""
    s3 = _client(endpoint, access_key, secret_key)
    objects: list[dict] = []
    paginator = s3.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=bucket, Prefix=prefix):
        objects.extend({"key": o["Key"], "size": o["Size"]} for o in page.get("Contents", []))
    return objects


def list_objects(endpoint: str, bucket: str, prefix: str = "",
                  access_key: str = DEFAULT_ACCESS_KEY, secret_key: str = DEFAULT_SECRET_KEY
                  ) -> list[dict]:
    """All objects under `prefix` in `bucket` as [{"key", "size"}, ...] (paginated), or [] on any
    connection/client error — a listing failure degrades the caller's KPI to "not yet seen", not a
    hard error, matching the previous `mc`-based helpers' best-effort semantics. Callers that need to
    tell an outage apart from a genuinely empty bucket should use `list_objects_strict` instead."""
    try:
        return list_objects_strict(endpoint, bucket, prefix, access_key, secret_key)
    except Exception:  # noqa: BLE001 — best-effort, see docstring
        return []


def list_keys(endpoint: str, bucket: str, prefix: str = "",
               access_key: str = DEFAULT_ACCESS_KEY, secret_key: str = DEFAULT_SECRET_KEY
               ) -> list[str]:
    return [o["key"] for o in list_objects(endpoint, bucket, prefix, access_key, secret_key)]


def total_bytes(endpoint: str, bucket: str, prefix: str = "",
                 access_key: str = DEFAULT_ACCESS_KEY, secret_key: str = DEFAULT_SECRET_KEY) -> int:
    return sum(o["size"] for o in list_objects(endpoint, bucket, prefix, access_key, secret_key))


def get_ilm_rule(endpoint: str, bucket: str, rule_id: str,
                  access_key: str = DEFAULT_ACCESS_KEY, secret_key: str = DEFAULT_SECRET_KEY
                  ) -> dict | None:
    """Best-effort verification that a named ILM/lifecycle rule is applied to the live bucket
    (GetBucketLifecycleConfiguration). None means "unknown" (no lifecycle configured at all, or the
    call failed) — not False — so the caller can tell that apart from "some rules exist, but not this
    one" ({"applied": False, ...})."""
    try:
        s3 = _client(endpoint, access_key, secret_key)
        resp = s3.get_bucket_lifecycle_configuration(Bucket=bucket)
    except Exception:  # noqa: BLE001 — includes NoSuchLifecycleConfiguration: no rules → unknown
        return None
    rules = resp.get("Rules", [])
    for rule in rules:
        if rule.get("ID") == rule_id:
            days = (rule.get("Expiration") or {}).get("Days")
            return {"applied": True, "days": days}
    return {"applied": False, "days": None} if rules else None
