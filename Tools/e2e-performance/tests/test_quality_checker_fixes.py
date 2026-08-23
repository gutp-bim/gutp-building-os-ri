"""
Regression tests for #342 and #343 (quality_checker.py bug fixes).

#342: check_api() previously called `/api/telemetry/search`, a route that has never existed in
      TelemetryController. It now probes `GET /health` as a lightweight reachability check.
#343: check_lake_parquet()'s schema-validity SQL only checked the legacy numeric `value` column.
      Since #152/ADR-0006 the Parquet lake uses a discriminated value (`value`/`value_text`/
      `value_bool`, `value_type`), so a string/boolean row legitimately has `value IS NULL` and
      must not be counted as schema_invalid.

check_lake_parquet() needs a live DuckDB/S3 lake and check_db() needs a live Postgres, so — matching
this suite's existing pattern (see test_smoke_api_integration.py) — #343 is covered via source-text
assertions rather than live execution. #342 is covered by mocking `requests.get`.

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_quality_checker_fixes.py -v
"""
from __future__ import annotations

import importlib.util
import inspect
from pathlib import Path
from unittest import mock

E2E_DIR = Path(__file__).parent.parent


def load_quality_checker():
    spec = importlib.util.spec_from_file_location(
        "quality_checker", E2E_DIR / "quality_checker.py"
    )
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def quality_checker_source() -> str:
    # Explicit UTF-8: quality_checker.py contains non-ASCII (em dashes) in its docstrings, and
    # read_text() would otherwise decode with the platform default (cp1252 on Windows).
    return (E2E_DIR / "quality_checker.py").read_text(encoding="utf-8")


def _function_source_slice(source: str, start_marker: str, end_marker: str) -> str:
    start = source.index(start_marker)
    end = source.index(end_marker, start)
    return source[start:end]


# ── #343: check_lake_parquet() validity predicate ──────────────────────────

def test_check_lake_parquet_validity_checks_value_text():
    source = quality_checker_source()
    fn_source = _function_source_slice(source, "def check_lake_parquet", "def check_db")
    assert "value_text" in fn_source, (
        "check_lake_parquet()'s validity check must treat a non-null value_text as schema-valid "
        "(#152/ADR-0006 discriminated value)"
    )


def test_check_lake_parquet_validity_checks_value_bool():
    source = quality_checker_source()
    fn_source = _function_source_slice(source, "def check_lake_parquet", "def check_db")
    assert "value_bool" in fn_source, (
        "check_lake_parquet()'s validity check must treat a non-null value_bool as schema-valid "
        "(#152/ADR-0006 discriminated value)"
    )


def test_check_lake_parquet_no_longer_uses_narrow_numeric_only_predicate():
    source = quality_checker_source()
    fn_source = _function_source_slice(source, "def check_lake_parquet", "def check_db")
    assert "AND value IS NOT NULL)" not in fn_source, (
        "the old numeric-only validity predicate must be gone — it false-flags string/boolean rows"
    )


def test_check_lake_parquet_references_adr_0006():
    source = quality_checker_source()
    fn_source = _function_source_slice(source, "def check_lake_parquet", "def check_db")
    assert "152" in fn_source or "ADR-0006" in fn_source, (
        "the validity predicate should document why it checks value_text/value_bool (#152/ADR-0006), "
        "so it doesn't silently regress"
    )


def test_check_db_unchanged_no_discriminated_columns():
    """TimescaleDB's `telemetry` table has no value_text/value_bool columns (ADR-0006 defers this
    to Phase B) — check_db() must not reference them or it will error against a real table."""
    source = quality_checker_source()
    fn_source = _function_source_slice(source, "def check_db", "def check_api")
    assert "value_text" not in fn_source
    assert "value_bool" not in fn_source


# ── #342: check_api() reachability probe ────────────────────────────────────

def test_check_api_calls_health_endpoint():
    qc = load_quality_checker()
    with mock.patch.object(qc.requests, "get") as mock_get:
        mock_get.return_value.status_code = 200
        qc.check_api("some-run-id", "http://localhost:5000")
    called_url = mock_get.call_args.args[0] if mock_get.call_args.args else mock_get.call_args.kwargs.get("url")
    assert called_url == "http://localhost:5000/health"


def test_check_api_no_longer_constructs_dead_endpoint_url():
    """The docstring may still mention the old route as historical context (#342), but the code
    must no longer build a URL from it."""
    source = quality_checker_source()
    assert 'f"{api_base}/api/telemetry/search"' not in source, (
        "check_api() must not construct the nonexistent /api/telemetry/search URL"
    )


def test_check_api_returns_one_on_200():
    qc = load_quality_checker()
    with mock.patch.object(qc.requests, "get") as mock_get:
        mock_get.return_value.status_code = 200
        assert qc.check_api("run", "http://localhost:5000") == 1


def test_check_api_returns_minus_one_on_non_200():
    qc = load_quality_checker()
    with mock.patch.object(qc.requests, "get") as mock_get:
        mock_get.return_value.status_code = 503
        assert qc.check_api("run", "http://localhost:5000") == -1


def test_check_api_returns_minus_one_on_request_exception():
    qc = load_quality_checker()
    with mock.patch.object(qc.requests, "get") as mock_get:
        mock_get.side_effect = qc.requests.exceptions.RequestException("boom")
        assert qc.check_api("run", "http://localhost:5000") == -1


def test_check_api_signature_unchanged_for_main_call_site():
    """main() and s17_scale_stage.py's positional call sites depend on this exact shape."""
    qc = load_quality_checker()
    sig = inspect.signature(qc.check_api)
    assert list(sig.parameters) == ["run_id", "api_base"]
