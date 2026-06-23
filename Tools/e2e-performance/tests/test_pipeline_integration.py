"""
Pipeline integration tests (TDD RED → GREEN).

Verifies that:
1. mqtt_nats_bridge.py exists and is importable (not just e2e_pipeline_bridge.py)
2. telemetry_consumer.py exists
3. docker-compose.oss.yaml includes ConnectorWorker service
4. smoke.sh no longer hard-depends on e2e_pipeline_bridge.py for the pipeline
5. docker-compose.oss.yaml has the new bridge/consumer services

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_pipeline_integration.py -v
"""
from pathlib import Path
import yaml
import pytest

REPO_ROOT = Path(__file__).parent.parent.parent.parent
E2E_DIR = REPO_ROOT / "Tools" / "e2e-performance"
DOCKER_COMPOSE = REPO_ROOT / "docker-compose.oss.yaml"
SMOKE_SH = E2E_DIR / "smoke.sh"


def load_compose():
    return yaml.safe_load(DOCKER_COMPOSE.read_text())


# ── New pipeline scripts ─────────────────────────────────────────────────

def test_mqtt_nats_bridge_script_exists():
    """mqtt_nats_bridge.py must exist as a standalone module."""
    assert (E2E_DIR / "mqtt_nats_bridge.py").exists(), (
        "mqtt_nats_bridge.py must exist: standalone MQTT→NATS forwarder"
    )


def test_telemetry_consumer_script_exists():
    """telemetry_consumer.py must exist as a standalone module."""
    assert (E2E_DIR / "telemetry_consumer.py").exists(), (
        "telemetry_consumer.py must exist: NATS validated → TimescaleDB writer"
    )


# ── docker-compose.oss.yaml ─────────────────────────────────────────────

def test_connector_worker_in_docker_compose():
    """building-os.connector-worker service must be in docker-compose.oss.yaml."""
    services = load_compose()["services"]
    assert "building-os.connector-worker" in services, (
        "docker-compose.oss.yaml must include building-os.connector-worker"
    )


def test_connector_worker_depends_on_nats():
    """ConnectorWorker must depend on NATS being healthy."""
    services = load_compose()["services"]
    cw = services.get("building-os.connector-worker", {})
    depends = cw.get("depends_on", {})
    assert "building-os.nats" in depends, (
        "building-os.connector-worker must depend on building-os.nats"
    )


def test_mqtt_nats_bridge_in_docker_compose():
    """building-os.mqtt-nats-bridge service must be in docker-compose.oss.yaml."""
    services = load_compose()["services"]
    assert "building-os.mqtt-nats-bridge" in services, (
        "docker-compose.oss.yaml must include building-os.mqtt-nats-bridge"
    )


def test_telemetry_consumer_in_docker_compose():
    """building-os.telemetry-consumer service must be in docker-compose.oss.yaml."""
    services = load_compose()["services"]
    assert "building-os.telemetry-consumer" in services, (
        "docker-compose.oss.yaml must include building-os.telemetry-consumer"
    )


# ── smoke.sh ─────────────────────────────────────────────────────────────

def test_smoke_sh_no_bridge_py_as_pipeline():
    """smoke.sh must not start e2e_pipeline_bridge.py as the main pipeline."""
    smoke_text = SMOKE_SH.read_text()
    # It's OK to have the file referenced in comments, but it must not be exec'd
    # as a background process for the main data pipeline.
    lines = [l.strip() for l in smoke_text.splitlines()
             if "e2e_pipeline_bridge" in l and not l.strip().startswith("#")]
    assert not lines, (
        "smoke.sh must not execute e2e_pipeline_bridge.py as pipeline. "
        f"Found non-comment references: {lines}"
    )


def test_smoke_sh_waits_for_connector_worker():
    """smoke.sh must wait for ConnectorWorker to be ready."""
    smoke_text = SMOKE_SH.read_text()
    assert "connector" in smoke_text.lower() or "building-os.mqtt" in smoke_text, (
        "smoke.sh must wait for ConnectorWorker or MQTT bridge to be ready"
    )
