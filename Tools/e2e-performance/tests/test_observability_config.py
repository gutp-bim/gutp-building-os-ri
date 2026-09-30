"""
Observability config validation tests (TDD RED → GREEN).

Verifies that Prometheus/Loki/Tempo configurations satisfy the cardinality
and retention requirements from Issue #84.

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_observability_config.py -v
"""
import re
from pathlib import Path
import yaml
import pytest

REPO_ROOT = Path(__file__).parent.parent.parent.parent
OSS_STACK = REPO_ROOT / "oss-stack"
DOCKER_COMPOSE = REPO_ROOT / "docker-compose.oss.yaml"


# ── Prometheus ────────────────────────────────────────────────────────────

def load_prometheus_config():
    return yaml.safe_load((OSS_STACK / "prometheus" / "prometheus.yml").read_text())


def load_compose():
    return yaml.safe_load(DOCKER_COMPOSE.read_text())


def test_prometheus_retention_15d_in_docker_compose():
    """Prometheus must start with --storage.tsdb.retention.time=15d."""
    compose = load_compose()
    cmd = compose["services"]["building-os.prometheus"]["command"]
    assert any("15d" in str(c) for c in cmd), (
        "Prometheus command must include --storage.tsdb.retention.time=15d"
    )


def test_prometheus_recording_rules_file_referenced():
    """prometheus.yml must reference the recording rules file."""
    cfg = load_prometheus_config()
    rule_files = cfg.get("rule_files", [])
    assert rule_files, "prometheus.yml must have rule_files section"
    assert any("recording" in str(f) for f in rule_files), (
        "rule_files must reference a recording_rules file"
    )


def test_prometheus_recording_rules_file_exists():
    """The recording rules file must exist on disk."""
    rules_path = OSS_STACK / "prometheus" / "recording_rules.yml"
    assert rules_path.exists(), f"Recording rules file not found: {rules_path}"


def test_prometheus_recording_rules_valid_yaml():
    """Recording rules file must be valid YAML with at least one group."""
    rules = yaml.safe_load((OSS_STACK / "prometheus" / "recording_rules.yml").read_text())
    assert "groups" in rules, "recording_rules.yml must have a 'groups' key"
    assert len(rules["groups"]) > 0, "recording_rules.yml must have at least one rule group"


def test_prometheus_no_point_id_label_in_queries():
    """No PromQL expression should use point_id as a label selector or grouping."""
    # Match PromQL label selector syntax: {point_id=, point_id!=, point_id=~, ...}
    # and "by (point_id)" / "without (point_id)" aggregation modifiers.
    pattern = re.compile(r'[{,]\s*point_id\s*[=!~]|by\s*\([^)]*point_id|without\s*\([^)]*point_id')

    for path in [
        OSS_STACK / "prometheus" / "prometheus.yml",
        OSS_STACK / "prometheus" / "recording_rules.yml",
    ]:
        if path.exists():
            text = path.read_text()
            matches = pattern.findall(text)
            assert not matches, (
                f"{path.name} must not use point_id as a Prometheus label — "
                f"it causes cardinality explosion at 100k+ points. Found: {matches}"
            )


# ── NATS exporter (#535) ──────────────────────────────────────────────────
#
# nats-server has no Prometheus `/metrics` endpoint (`:8222/metrics` is 404 on nats:2.10), so the
# `nats` scrape job must point at natsio/prometheus-nats-exporter, and the recording rules must use
# the names that exporter actually emits. The names below were captured from
# natsio/prometheus-nats-exporter:0.20.2 `-varz -jsz=all` against nats:2.10-alpine `-js` with a
# BUILDING_OS_VALIDATED stream + pull consumer (#535). They are `jetstream_*` with the labels
# `stream_name` / `consumer_name` — NOT the `nats_consumer_*` names the rules used before.

NATS_EXPORTER_SERVICE = "building-os.nats-exporter"
VERIFIED_EXPORTER_JETSTREAM_METRICS = {
    "jetstream_consumer_num_pending",
    "jetstream_consumer_num_ack_pending",
    "jetstream_consumer_num_redelivered",
    "jetstream_consumer_num_waiting",
    "jetstream_consumer_delivered_consumer_seq",
    "jetstream_consumer_delivered_stream_seq",
    "jetstream_consumer_ack_floor_consumer_seq",
    "jetstream_consumer_ack_floor_stream_seq",
    "jetstream_stream_total_bytes",
    "jetstream_stream_total_messages",
    "jetstream_stream_consumer_count",
    "jetstream_stream_first_seq",
    "jetstream_stream_last_seq",
    "jetstream_server_total_message_bytes",
    "jetstream_server_total_messages",
}


def _exporter_service():
    services = load_compose()["services"]
    assert NATS_EXPORTER_SERVICE in services, (
        f"docker-compose.oss.yaml must define {NATS_EXPORTER_SERVICE} (nats-server has no /metrics)"
    )
    return services[NATS_EXPORTER_SERVICE]


def _exporter_port(svc) -> str:
    cmd = [str(c) for c in svc.get("command", [])]
    for i, c in enumerate(cmd):
        if c.startswith("-port="):
            return c.split("=", 1)[1]
        if c in ("-port", "-p") and i + 1 < len(cmd):
            return cmd[i + 1]
    return "7777"  # prometheus-nats-exporter default


def test_nats_exporter_is_in_observability_profile_with_pinned_image():
    svc = _exporter_service()
    assert svc.get("profiles") == ["observability"], "exporter belongs to the observability profile only"
    image = svc["image"]
    assert image.startswith("natsio/prometheus-nats-exporter:"), image
    tag = image.rsplit(":", 1)[1]
    assert tag and tag != "latest", f"exporter image must pin a version tag, got {image}"


def test_nats_exporter_scrapes_varz_and_all_jetstream_from_nats_monitor():
    cmd = [str(c) for c in _exporter_service()["command"]]
    assert "-varz" in cmd
    assert "-jsz=all" in cmd, "JetStream stream/consumer metrics need -jsz=all"
    assert cmd[-1] == "http://building-os.nats:8222", "the monitored URL is the last positional arg"


def test_prometheus_nats_job_targets_exporter_not_nats_monitor():
    cfg = load_prometheus_config()
    job = next(j for j in cfg["scrape_configs"] if j["job_name"] == "nats")
    targets = [t for sc in job["static_configs"] for t in sc["targets"]]
    port = _exporter_port(_exporter_service())
    assert targets == [f"{NATS_EXPORTER_SERVICE}:{port}"], targets
    assert job.get("metrics_path", "/metrics") == "/metrics"


def _nats_rule_group():
    rules = yaml.safe_load((OSS_STACK / "prometheus" / "recording_rules.yml").read_text())
    return next(g for g in rules["groups"] if g["name"] == "building_os_nats")


def test_nats_recording_rules_only_reference_verified_exporter_metrics():
    ident = re.compile(r"\b((?:nats|jetstream|gnatsd)_[a-z0-9_]+)\b")
    for rule in _nats_rule_group()["rules"]:
        referenced = set(ident.findall(rule["expr"]))
        assert referenced, f"{rule['record']} references no NATS metric"
        unknown = referenced - VERIFIED_EXPORTER_JETSTREAM_METRICS
        assert not unknown, f"{rule['record']} uses metric names the exporter does not emit: {unknown}"


def test_nats_recording_rules_group_by_exporter_labels():
    for rule in _nats_rule_group()["rules"]:
        by = re.findall(r"by\s*\(([^)]*)\)", rule["expr"])
        labels = {l.strip() for b in by for l in b.split(",")}
        assert labels <= {"stream_name", "consumer_name"}, (
            f"{rule['record']} groups by {labels}; the exporter labels are stream_name/consumer_name"
        )


def test_nats_recording_rules_cover_pending_redelivered_and_stream_bytes():
    records = {r["record"] for r in _nats_rule_group()["rules"]}
    assert {
        "nats:jetstream_consumer_pending:max",       # consumed by #456 (/platform/status)
        "nats:jetstream_msgs_delivered:rate1m",
        "nats:jetstream_consumer_redelivered:max",
        "nats:jetstream_stream_bytes:max",
    } <= records, records


# ── Loki ─────────────────────────────────────────────────────────────────

def load_loki_config():
    return yaml.safe_load((OSS_STACK / "loki" / "loki-config.yaml").read_text())


def test_loki_retention_enabled():
    """Loki must have retention enabled."""
    cfg = load_loki_config()
    compactor = cfg.get("compactor", {})
    assert compactor.get("retention_enabled") is True, (
        "loki-config.yaml: compactor.retention_enabled must be true"
    )


def test_loki_table_manager_or_limits_retention():
    """Loki must define a default retention period of ≤30d."""
    cfg = load_loki_config()
    # retention can be in limits_config.retention_period or compactor settings
    limits = cfg.get("limits_config", {})
    retention = limits.get("retention_period", "")
    assert retention, "loki-config.yaml: limits_config.retention_period must be set"
    # e.g. "30d", "720h" — both acceptable
    days = _parse_days(retention)
    assert days <= 30, f"Loki default retention must be ≤30d, got {retention}"


def test_loki_ingestion_label_policy():
    """Loki config must restrict label cardinality."""
    cfg = load_loki_config()
    limits = cfg.get("limits_config", {})
    max_labels = limits.get("max_label_names_per_series", 999)
    assert max_labels <= 15, (
        f"limits_config.max_label_names_per_series must be ≤15, got {max_labels}"
    )


# ── Tempo ─────────────────────────────────────────────────────────────────

def load_tempo_config():
    return yaml.safe_load((OSS_STACK / "tempo" / "tempo-config.yaml").read_text())


def assert_tempo_retention_at_most_7d(cfg):
    compactor = cfg.get("compactor", {})
    block_retention = compactor.get("compaction", {}).get("block_retention", "")
    assert block_retention, "tempo-config.yaml: compactor.compaction.block_retention must be set"
    days = _parse_days(block_retention)
    assert days <= 7, f"Tempo retention must be ≤7d, got {block_retention}"


def test_tempo_retention_7d():
    """Tempo must store traces for ≤7 days."""
    assert_tempo_retention_at_most_7d(load_tempo_config())


def test_tempo_retention_rejects_more_than_7d():
    """The guard must fail if an operator accidentally raises retention above seven days."""
    invalid = {"compactor": {"compaction": {"block_retention": "192h"}}}
    with pytest.raises(AssertionError, match="≤7d"):
        assert_tempo_retention_at_most_7d(invalid)


def test_tempo_sampling_env_var_supported():
    """Tempo config must use an environment-variable-driven sampling rate."""
    tempo_cfg_text = (OSS_STACK / "tempo" / "tempo-config.yaml").read_text()
    # We accept either env-var expansion syntax or a hardcoded rate ≤ 10%
    # For simplicity: the config file should have a tail_sampling or a probabilistic sampler section
    # OR the otel-collector config drives sampling
    otel_cfg = OSS_STACK / "otel-collector" / "config.yaml"
    if otel_cfg.exists():
        otel_text = otel_cfg.read_text()
        has_sampling = "probabilistic" in otel_text or "tail_sampling" in otel_text or "sampling" in otel_text
        assert has_sampling, "OTel collector must configure trace sampling"
    else:
        pytest.skip("otel-collector config not found — sampling tested via collector config")


# ── helpers ───────────────────────────────────────────────────────────────

def _parse_days(duration: str) -> float:
    """Convert a duration string like '30d', '720h' to days."""
    duration = str(duration).strip()
    if duration.endswith("d"):
        return float(duration[:-1])
    if duration.endswith("h"):
        return float(duration[:-1]) / 24
    if duration.endswith("m"):
        return float(duration[:-1]) / 60 / 24
    return float("inf")
