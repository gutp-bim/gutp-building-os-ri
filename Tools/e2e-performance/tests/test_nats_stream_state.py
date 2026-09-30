"""NATS stream bytes / redelivery in the E10 soak (#535, #297 remainder).

E10 already polls NATS `/jsz` for consumer pending. #297 also asked for the stream's storage
footprint and redelivery, which `/jsz?consumers=1&streams=1` carries in the same payload. These are
report-only KPIs (no threshold yet — there is no measured baseline).

The `/jsz` fixture below is the real payload shape captured from nats:2.10-alpine `-js` with a
BUILDING_OS_VALIDATED stream and one pull consumer (#535 verification run).

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_nats_stream_state.py -v
"""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from unittest import mock

import pytest
import yaml

E2E_DIR = Path(__file__).parent.parent
REPO_ROOT = E2E_DIR.parent.parent
sys.path.insert(0, str(E2E_DIR))

import kpi_sampler  # noqa: E402


def _consumer(stream: str, name: str, pending: int, redelivered: int) -> dict:
    return {
        "stream_name": stream,
        "name": name,
        "delivered": {"consumer_seq": 6, "stream_seq": 3},
        "ack_floor": {"consumer_seq": 0, "stream_seq": 0},
        "num_ack_pending": 3,
        "num_redelivered": redelivered,
        "num_waiting": 0,
        "num_pending": pending,
    }


def _stream(name: str, bytes_: int, messages: int, consumers: list[dict]) -> dict:
    return {
        "name": name,
        "state": {"messages": messages, "bytes": bytes_, "first_seq": 1, "last_seq": messages,
                  "num_subjects": 1, "consumer_count": len(consumers)},
        "consumer_detail": consumers,
    }


def _jsz(*streams: dict) -> dict:
    return {
        "streams": len(streams),
        "bytes": sum(s["state"]["bytes"] for s in streams),
        "account_details": [{"name": "$G", "id": "$G", "stream_detail": list(streams)}],
    }


JSZ = _jsz(
    _stream("BUILDING_OS_VALIDATED", 1260, 20, [
        _consumer("BUILDING_OS_VALIDATED", "lake-writer", 17, 3),
        _consumer("BUILDING_OS_VALIDATED", "hot-kv", 0, 1),
    ]),
    _stream("BUILDING_OS_RAW", 999_999, 50, [_consumer("BUILDING_OS_RAW", "hvac", 5, 40)]),
)


# ── kpi_sampler.stream_state_from_jsz ─────────────────────────────────────────


def test_stream_state_sums_bytes_messages_and_redelivered_for_matching_streams():
    state = kpi_sampler.stream_state_from_jsz(JSZ, "VALIDATED")
    assert state == {"bytes": 1260, "messages": 20, "redelivered": 4}


def test_stream_state_filter_is_case_insensitive_substring():
    assert kpi_sampler.stream_state_from_jsz(JSZ, "validated")["bytes"] == 1260


def test_stream_state_empty_filter_covers_every_stream():
    state = kpi_sampler.stream_state_from_jsz(JSZ, "")
    assert state == {"bytes": 1260 + 999_999, "messages": 70, "redelivered": 44}


def test_stream_state_is_zero_when_no_stream_matches():
    assert kpi_sampler.stream_state_from_jsz(JSZ, "NOPE") == {"bytes": 0, "messages": 0, "redelivered": 0}


def test_sample_stream_state_reads_jsz_with_streams_and_consumers():
    resp = mock.Mock()
    resp.json.return_value = JSZ
    with mock.patch.object(kpi_sampler.requests, "get", return_value=resp) as get:
        state = kpi_sampler.sample_stream_state("http://nats:8222/", "VALIDATED")
    assert state["bytes"] == 1260
    url = get.call_args.args[0]
    assert url.startswith("http://nats:8222/jsz?")
    assert "consumers=1" in url and "streams=1" in url


# ── s19: tick + summary ───────────────────────────────────────────────────────


def load_s19():
    spec = importlib.util.spec_from_file_location("s19_endurance_soak", E2E_DIR / "s19_endurance_soak.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _patched_tick(s19, stream_state=None, stream_exc=None):
    stream_mock = (mock.patch.object(s19.kpis, "sample_stream_state", side_effect=stream_exc)
                   if stream_exc else
                   mock.patch.object(s19.kpis, "sample_stream_state", return_value=stream_state))
    with mock.patch.object(s19, "docker_stats", return_value={}), \
         mock.patch.object(s19, "docker_restart_state", return_value={}), \
         mock.patch.object(s19, "probe_health", return_value={}), \
         mock.patch.object(s19.kpis, "sample_pending", return_value=(0, {})), \
         stream_mock:
        return s19.resource_sample_tick(["building-os.connector-worker"], {}, prom_url="")


def test_resource_tick_records_validated_stream_state():
    s19 = load_s19()
    tick = _patched_tick(s19, stream_state={"bytes": 2048, "messages": 9, "redelivered": 2})
    assert tick["nats_stream"] == {"bytes": 2048, "messages": 9, "redelivered": 2}


def test_resource_tick_records_null_stream_state_when_nats_monitor_unreachable():
    s19 = load_s19()
    tick = _patched_tick(s19, stream_exc=s19.requests.RequestException("down"))
    assert tick["nats_stream"] is None


def _sample(elapsed_h: float, stream: dict | None) -> dict:
    rec = {
        "ts": "2026-10-01T00:00:00+00:00",
        "elapsed_s": elapsed_h * 3600.0,
        "mem_mib": {},
        "restarts": {},
        "health": {},
        "consumer_pending_total": 0,
        "consumer_pending": {},
        "nats_stream": stream,
    }
    return rec


MIB = 1024 * 1024


def test_summarize_resources_reports_stream_bytes_and_redelivery():
    s19 = load_s19()
    samples = [
        _sample(0.0, {"bytes": 100 * MIB, "messages": 1, "redelivered": 0}),
        _sample(1.0, {"bytes": 110 * MIB, "messages": 2, "redelivered": 5}),
        _sample(2.0, {"bytes": 120 * MIB, "messages": 3, "redelivered": 1}),
        _sample(3.0, {"bytes": 130 * MIB, "messages": 4, "redelivered": 0}),
    ]
    m = s19.summarize_resources(samples, [])
    assert m["nats_validated_stream_mib_max"] == pytest.approx(130.0)
    assert m["nats_validated_stream_mib_last"] == pytest.approx(130.0)
    # 後半（2h, 3h）だけの回帰スロープ — RSS と同じ methodology
    assert m["nats_validated_stream_mib_growth_per_hour"] == pytest.approx(10.0)
    assert m["nats_validated_redelivered_max"] == 5
    assert m["nats_validated_redelivered_last"] == 0
    assert m["nats_stream_samples"] == 4


def test_summarize_resources_skips_unreachable_and_legacy_ticks():
    """None（NATS 監視不達）と、#535 以前の jsonl（キー自体が無い）は数えない。"""
    s19 = load_s19()
    legacy = _sample(0.0, None)
    del legacy["nats_stream"]
    samples = [legacy, _sample(1.0, None), _sample(2.0, {"bytes": MIB, "messages": 1, "redelivered": 0})]
    m = s19.summarize_resources(samples, [])
    assert m["nats_stream_samples"] == 1
    assert m["nats_validated_stream_mib_max"] == pytest.approx(1.0)


def test_summarize_resources_omits_stream_kpis_without_samples():
    """値が取れなかった run では KPI 自体を出さない（gate は absent → SKIP）。0 を書くと
    「計測した結果 0」と読めてしまう。"""
    s19 = load_s19()
    samples = [_sample(0.0, None)]
    m = s19.summarize_resources(samples, [])
    assert "nats_validated_stream_mib_max" not in m
    assert "nats_validated_redelivered_max" not in m
    assert m["nats_stream_samples"] == 0


# ── thresholds ────────────────────────────────────────────────────────────────


def test_kpi_thresholds_report_stream_bytes_and_redelivery_without_gating():
    data = yaml.safe_load((REPO_ROOT / "e2e" / "kpi-thresholds.yaml").read_text(encoding="utf-8"))
    e10 = data["axes"]["E10_endurance_soak"]
    for key in (
        "nats_validated_stream_mib_max",
        "nats_validated_stream_mib_last",
        "nats_validated_stream_mib_growth_per_hour",
        "nats_validated_redelivered_max",
        "nats_validated_redelivered_last",
    ):
        assert key in e10, key
        assert e10[key]["op"] == "report", f"{key} has no measured baseline yet — report only"
