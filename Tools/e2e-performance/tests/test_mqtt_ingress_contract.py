"""Contract checks for the current MQTT → NATS ingestion boundary."""

from pathlib import Path


REPO_ROOT = Path(__file__).parent.parent.parent.parent
WORKER = REPO_ROOT / "DotNet" / "BuildingOS.ConnectorWorker"
INGRESS = WORKER / "Connectors" / "MqttIngressWorker.cs"
REGISTRATION = WORKER / "Startup" / "ConnectorWorkerServiceCollectionExtensions.cs"


def test_mqtt_ingress_publishes_to_raw_mqtt_subject():
    """The transport worker must publish its envelope to the canonical raw subject."""
    source = INGRESS.read_text()
    assert 'RawMqttSubject = "building-os.raw.mqtt"' in source
    assert "publisher.PublishAsync(RawMqttSubject" in source


def test_mqtt_ingress_requires_tenant_and_device_topic_segments():
    """Ambiguous MQTT topics must be rejected before publishing to NATS."""
    source = INGRESS.read_text()
    assert "topic.Split('/', 3)" in source
    assert "string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(deviceId)" in source


def test_connector_worker_registers_transport_and_normalizer_together():
    """Enabling MQTT must register both halves of the in-process pipeline."""
    source = REGISTRATION.read_text()
    assert "AddHostedService(sp => new MqttIngressWorker(" in source
    assert "AddHostedService(sp => new MqttConnectorWorker(" in source


def test_connector_worker_only_enables_mqtt_when_host_is_configured():
    """The optional MQTT path must remain disabled when MQTT_HOST is empty."""
    source = REGISTRATION.read_text()
    assert 'builder.Configuration["MQTT_HOST"]?.Trim()' in source
    assert "if (string.IsNullOrWhiteSpace(mqttHost)) return;" in source
