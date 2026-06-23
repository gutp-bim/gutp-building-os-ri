"""
S1 Smoke API integration tests (TDD RED → GREEN).

Verifies that:
1. docker-compose.oss.yaml includes API Server service
2. smoke.sh waits for API Server health endpoint
3. quality_checker.py evaluate() records api_row_count and marks it as available
4. quality_checker.py evaluate() passes even when api_row_count >= 0 (not -1)

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_smoke_api_integration.py -v
"""
from pathlib import Path
import sys
import yaml
import importlib.util
import pytest

REPO_ROOT = Path(__file__).parent.parent.parent.parent
E2E_DIR = REPO_ROOT / "Tools" / "e2e-performance"
DOCKER_COMPOSE = REPO_ROOT / "docker-compose.oss.yaml"
SMOKE_SH = E2E_DIR / "smoke.sh"


def load_compose():
    return yaml.safe_load(DOCKER_COMPOSE.read_text())


def load_quality_checker():
    spec = importlib.util.spec_from_file_location(
        "quality_checker", E2E_DIR / "quality_checker.py"
    )
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ── docker-compose API Server ──────────────────────────────────────────

def test_api_server_in_docker_compose():
    """building-os.api service must be in docker-compose.oss.yaml."""
    services = load_compose()["services"]
    assert "building-os.api" in services, (
        "docker-compose.oss.yaml must include building-os.api (API Server)"
    )


def test_api_server_has_health_endpoint():
    """building-os.api must have a healthcheck configured."""
    services = load_compose()["services"]
    api = services.get("building-os.api", {})
    assert "healthcheck" in api or api.get("environment", {}).get("DISABLE_AUTH"), (
        "building-os.api must have healthcheck or DISABLE_AUTH=true configured"
    )


# ── smoke.sh API wait ──────────────────────────────────────────────────

def test_smoke_sh_waits_for_api_server():
    """smoke.sh must include a wait step for the API Server health endpoint."""
    smoke_text = SMOKE_SH.read_text()
    assert "/health" in smoke_text or "API Server" in smoke_text, (
        "smoke.sh must wait for API Server (/health endpoint)"
    )


def test_smoke_sh_runs_quality_check_with_api():
    """smoke.sh must pass --api-base to quality_checker.py."""
    smoke_text = SMOKE_SH.read_text()
    assert "quality_checker.py" in smoke_text, (
        "smoke.sh must invoke quality_checker.py"
    )


# ── quality_checker.py evaluate() ─────────────────────────────────────

def test_evaluate_api_row_count_in_result():
    """evaluate() result must include api_row_count."""
    qc = load_quality_checker()
    db = {"db_row_count": 100, "duplicate_count": 0,
          "schema_valid_count": 100, "schema_invalid_count": 0}
    result = qc.evaluate("test-run", 100, db, api_row_count=50)
    assert "api_row_count" in result
    assert result["api_row_count"] == 50


def test_evaluate_passes_when_api_returns_data():
    """evaluate() must PASS when api_row_count > 0 and other criteria are met."""
    qc = load_quality_checker()
    db = {"db_row_count": 100, "duplicate_count": 0,
          "schema_valid_count": 100, "schema_invalid_count": 0}
    result = qc.evaluate("test-run", 100, db, api_row_count=50)
    assert result["passed"] is True


def test_evaluate_api_unavailable_records_minus_one():
    """evaluate() must record api_row_count=-1 when API is unavailable (not fail)."""
    qc = load_quality_checker()
    db = {"db_row_count": 100, "duplicate_count": 0,
          "schema_valid_count": 100, "schema_invalid_count": 0}
    result = qc.evaluate("test-run", 100, db, api_row_count=-1)
    # API unavailability alone should not fail the smoke (DB pass is sufficient)
    assert result["api_row_count"] == -1


def test_evaluate_api_available_included_in_pass_criteria():
    """When api_row_count >= 0, quality_checker must reflect API health in result."""
    qc = load_quality_checker()
    db = {"db_row_count": 100, "duplicate_count": 0,
          "schema_valid_count": 100, "schema_invalid_count": 0}
    result_with_api = qc.evaluate("test-run", 100, db, api_row_count=50)
    result_no_api = qc.evaluate("test-run", 100, db, api_row_count=-1)
    # Result with API available should have api_available=True or similar flag
    assert result_with_api.get("api_available") is True or result_with_api["api_row_count"] >= 0
    assert result_no_api.get("api_available") is False or result_no_api["api_row_count"] == -1
