"""
Minimum profile configuration tests (TDD RED → GREEN).

Verifies that docker-compose.minimal.yaml and values-minimal.yaml satisfy
the requirements from Issue #70.

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_minimum_profile.py -v
"""
from pathlib import Path
import yaml
import pytest

REPO_ROOT = Path(__file__).parent.parent.parent.parent
MINIMAL_COMPOSE = REPO_ROOT / "docker-compose.minimal.yaml"
HELM_MINIMAL = REPO_ROOT / "kubernetes" / "helm" / "building-os" / "values-minimal.yaml"
MAKEFILE = REPO_ROOT / "Makefile"


def load_minimal_compose():
    assert MINIMAL_COMPOSE.exists(), f"docker-compose.minimal.yaml not found at {MINIMAL_COMPOSE}"
    return yaml.safe_load(MINIMAL_COMPOSE.read_text())


def load_helm_minimal():
    assert HELM_MINIMAL.exists(), f"values-minimal.yaml not found at {HELM_MINIMAL}"
    return yaml.safe_load(HELM_MINIMAL.read_text())


# ── docker-compose.minimal.yaml ──────────────────────────────────────────

def test_minimal_compose_file_exists():
    assert MINIMAL_COMPOSE.exists(), "docker-compose.minimal.yaml must exist"


def test_minimal_compose_valid_yaml():
    compose = load_minimal_compose()
    assert isinstance(compose, dict)
    assert "services" in compose


def test_minimal_compose_required_services():
    """NATS, TimescaleDB, and pgBouncer (transaction pool) must be present."""
    services = load_minimal_compose()["services"]
    assert "building-os.nats" in services, "minimal profile must include NATS"
    assert "building-os.postgres" in services, "minimal profile must include TimescaleDB"
    assert "building-os.pgbouncer" in services, "minimal profile must include pgBouncer"


def test_minimal_compose_excludes_heavy_services():
    """OxiGraph, Keycloak, MinIO, Prometheus, Grafana, Loki, Tempo must be absent."""
    services = load_minimal_compose()["services"]
    excluded = [
        "building-os.oxigraph",
        "building-os.keycloak",
        "building-os.minio",
        "building-os.prometheus",
        "building-os.grafana",
        "building-os.loki",
        "building-os.tempo",
    ]
    present = [s for s in excluded if s in services]
    assert not present, (
        f"Minimal profile must NOT include: {present}"
    )


def test_minimal_compose_disable_auth_env():
    """API Server (if present) must have DISABLE_AUTH=true or no auth requirement."""
    compose = load_minimal_compose()
    services = compose.get("services", {})
    # Minimal compose may not include the API server container itself,
    # but if it does, DISABLE_AUTH must be set.
    api_service = services.get("building-os.api")
    if api_service:
        env = api_service.get("environment", {})
        disable_auth = env.get("DISABLE_AUTH", "false")
        assert str(disable_auth).lower() == "true", (
            "Minimal profile API service must have DISABLE_AUTH=true"
        )


# ── Helm values-minimal.yaml ──────────────────────────────────────────────

def test_helm_minimal_file_exists():
    assert HELM_MINIMAL.exists(), "kubernetes/helm/building-os/values-minimal.yaml must exist"


def test_helm_minimal_valid_yaml():
    values = load_helm_minimal()
    assert isinstance(values, dict)


def test_helm_minimal_heavy_components_disabled():
    """OxiGraph, Keycloak, MinIO, observability must be disabled in minimal values."""
    values = load_helm_minimal()
    disabled_checks = {
        "oxigraph.enabled": values.get("oxigraph", {}).get("enabled", True),
        "keycloak.enabled": values.get("keycloak", {}).get("enabled", True),
        "minio.enabled": values.get("minio", {}).get("enabled", True),
        "prometheus.enabled": values.get("prometheus", {}).get("enabled", True),
        "grafana.enabled": values.get("grafana", {}).get("enabled", True),
    }
    enabled = {k: v for k, v in disabled_checks.items() if v is True}
    assert not enabled, (
        f"Minimal Helm values must disable these components: {list(enabled.keys())}"
    )


def test_helm_minimal_pgbouncer_enabled():
    """pgBouncer must be enabled in minimal profile."""
    values = load_helm_minimal()
    pgbouncer = values.get("pgbouncer", {})
    assert pgbouncer.get("enabled", False) is True, (
        "values-minimal.yaml must have pgbouncer.enabled=true"
    )


# ── Makefile ──────────────────────────────────────────────────────────────

def test_makefile_minimal_targets():
    """Makefile must have local-up-minimal and local-down-minimal targets."""
    makefile_text = MAKEFILE.read_text()
    assert "local-up-minimal" in makefile_text, (
        "Makefile must have a local-up-minimal target"
    )
    assert "local-down-minimal" in makefile_text, (
        "Makefile must have a local-down-minimal target"
    )


def test_makefile_minimal_uses_compose_minimal():
    """local-up-minimal target must reference docker-compose.minimal.yaml."""
    makefile_text = MAKEFILE.read_text()
    assert "docker-compose.minimal.yaml" in makefile_text, (
        "Makefile local-up-minimal must reference docker-compose.minimal.yaml"
    )
