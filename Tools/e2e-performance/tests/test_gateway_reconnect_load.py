import importlib.util
from pathlib import Path
import sys


MODULE_PATH = Path(__file__).parents[1] / "s18_gateway_reconnect.py"
SPEC = importlib.util.spec_from_file_location("gateway_reconnect", MODULE_PATH)
gateway_reconnect = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = gateway_reconnect
SPEC.loader.exec_module(gateway_reconnect)


def test_gateway_ids_are_deterministic_and_unique():
    ids = gateway_reconnect.gateway_ids(100, "20260722T120000Z-s18")

    assert ids == gateway_reconnect.gateway_ids(100, "20260722T120000Z-s18")
    assert len(ids) == len(set(ids)) == 100
    assert ids[0].startswith("GW-S18-")


def test_topology_is_isolated_between_runs():
    first = gateway_reconnect.build_topology(100, "20260722T143000Z-s18")
    second = gateway_reconnect.build_topology(100, "20260722T144000Z-s18")

    assert not ({p["point_id"] for p in first} & {p["point_id"] for p in second})
    assert not ({p["building_id"] for p in first} & {p["building_id"] for p in second})


def test_reconnect_offsets_are_deterministic_and_bounded():
    offsets = gateway_reconnect.reconnect_offsets_ms(100, concentration_ms=500, seed=18)

    assert offsets == gateway_reconnect.reconnect_offsets_ms(100, 500, 18)
    assert min(offsets) >= 0
    assert max(offsets) <= 500


def test_evaluation_reports_all_kpis_and_threshold_failures():
    result = gateway_reconnect.evaluate(
        gateway_count=100,
        reconnected=99,
        convergence_ms=6_000,
        ingress_accepted=99,
        ingress_expected=100,
        ingress_rejected=1,
        lake_rows=98,
        duplicate_rows=2,
        controls_accepted=99,
        controls_succeeded=98,
        service_errors={"gateway-bridge": 1, "connector-worker": 0, "api-server": 0},
        thresholds=gateway_reconnect.Thresholds(convergence_ms=5_000, loss_rate=0, duplicates=0,
                                                control_success_rate=1, service_errors=0),
    )

    assert result["metrics"]["loss"] == 2
    assert result["metrics"]["control_success_rate"] == 0.98
    assert result["passed"] is False
    assert set(result["exceeded_thresholds"]) == {
        "reconnected", "convergence_ms", "loss_rate", "duplicates",
        "control_success_rate", "service_errors",
    }


def test_passing_evaluation_renders_auditable_report():
    result = gateway_reconnect.evaluate(
        gateway_count=100, reconnected=100, convergence_ms=2_500,
        ingress_accepted=100, ingress_expected=100, ingress_rejected=100,
        lake_rows=100, duplicate_rows=0, controls_accepted=100, controls_succeeded=100,
        service_errors={"gateway-bridge": 0, "connector-worker": 0, "api-server": 0},
        thresholds=gateway_reconnect.Thresholds(),
    )

    report = gateway_reconnect.render_markdown(result, "run-1", concentration_ms=500)
    assert result["passed"] is True
    assert "100 Gateway" in report
    assert "PASS" in report
    assert "gateway-bridge" in report
    assert "500 ms" in report
