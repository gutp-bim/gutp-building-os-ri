"""
Pipeline integration tests (TDD RED → GREEN).

Verifies that:
1. the retired Python MQTT bridge is absent
2. telemetry_consumer.py exists
3. docker-compose.oss.yaml includes ConnectorWorker service
4. smoke.sh no longer hard-depends on e2e_pipeline_bridge.py for the pipeline
5. docker-compose.oss.yaml configures the in-process MQTT ingress and consumer

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

def test_retired_mqtt_nats_bridge_script_is_absent():
    """MQTT ingress belongs to ConnectorWorker, so the retired sidecar must stay absent."""
    assert not (E2E_DIR / "mqtt_nats_bridge.py").exists()


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


def assert_mqtt_ingress_owned_by_connector_worker(services):
    """Reject the retired sidecar and require the ConnectorWorker MQTT boundary."""
    assert "building-os.mqtt-nats-bridge" not in services, (
        "the retired standalone MQTT→NATS bridge must not be restored"
    )
    connector = services.get("building-os.connector-worker", {})
    environment = connector.get("environment", {})
    assert "MQTT_HOST" in environment, "ConnectorWorker must accept MQTT_HOST"
    assert environment.get("MQTT_TOPIC_FILTER") == "telemetry/#", (
        "ConnectorWorker must subscribe to the canonical telemetry topic filter"
    )


def test_mqtt_ingress_is_owned_by_connector_worker():
    """MQTT ingress runs inside ConnectorWorker, not in a retired Python sidecar."""
    services = load_compose()["services"]
    assert_mqtt_ingress_owned_by_connector_worker(services)


def test_mqtt_ingress_contract_rejects_retired_bridge():
    """The config guard must fail if the retired bridge is added again."""
    services = load_compose()["services"] | {"building-os.mqtt-nats-bridge": {}}
    with pytest.raises(AssertionError, match="must not be restored"):
        assert_mqtt_ingress_owned_by_connector_worker(services)


def test_telemetry_consumer_in_docker_compose():
    """building-os.telemetry-consumer service must be in docker-compose.oss.yaml."""
    services = load_compose()["services"]
    assert "building-os.telemetry-consumer" in services, (
        "docker-compose.oss.yaml must include building-os.telemetry-consumer"
    )


# ── smoke.sh ─────────────────────────────────────────────────────────────

def test_smoke_sh_no_python_bridge_as_pipeline():
    """smoke.sh must use ConnectorWorker rather than either retired Python bridge."""
    smoke_text = SMOKE_SH.read_text()
    assert "e2e_pipeline_bridge.py" not in smoke_text
    assert "mqtt_nats_bridge.py" not in smoke_text


def test_smoke_sh_waits_for_connector_worker():
    """smoke.sh must wait for ConnectorWorker to be ready."""
    smoke_text = SMOKE_SH.read_text()
    assert "connector" in smoke_text.lower() or "building-os.mqtt" in smoke_text, (
        "smoke.sh must wait for ConnectorWorker or MQTT bridge to be ready"
    )
