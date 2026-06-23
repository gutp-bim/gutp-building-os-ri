"""
#98: mqtt_nats_bridge.py が building-os.validated.telemetry を NATS へ転送することを検証する。

TDD サイクル:
  1. validated topic/stream の定数が定義されている
  2. _ensure_stream がストリーム名 BUILDING_OS_VALIDATED と subject を渡して呼ばれる
  3. MQTT validated メッセージが NATS の同名 subject へ publish される

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_mqtt_nats_bridge_validated.py -v
"""
import sys
from pathlib import Path
import importlib
import asyncio
from unittest.mock import AsyncMock, MagicMock, patch, call

E2E_DIR = Path(__file__).parent.parent
sys.path.insert(0, str(E2E_DIR))


# ── Cycle 1: 定数 ────────────────────────────────────────────────────────────

def test_bridge_defines_validated_stream_constant():
    """mqtt_nats_bridge は _STREAM_VALIDATED = 'BUILDING_OS_VALIDATED' を定義している必要がある。"""
    import mqtt_nats_bridge as bridge
    assert hasattr(bridge, "_STREAM_VALIDATED") or "BUILDING_OS_VALIDATED" in (
        getattr(bridge, "_STREAM_VALIDATED", "") or ""
    ), "mqtt_nats_bridge must define _STREAM_VALIDATED = 'BUILDING_OS_VALIDATED'"
    assert bridge._STREAM_VALIDATED == "BUILDING_OS_VALIDATED"


def test_bridge_defines_validated_subject_constant():
    """mqtt_nats_bridge は _SUBJECT_VALIDATED = 'building-os.validated.telemetry' を定義している必要がある。"""
    import mqtt_nats_bridge as bridge
    assert hasattr(bridge, "_SUBJECT_VALIDATED"), (
        "mqtt_nats_bridge must define _SUBJECT_VALIDATED"
    )
    assert bridge._SUBJECT_VALIDATED == "building-os.validated.telemetry"


# ── Cycle 2: run() が validated ストリームを作成すること ─────────────────────

def test_run_creates_validated_stream():
    """run() は起動時に BUILDING_OS_VALIDATED ストリームを building-os.validated.telemetry で作成する。"""
    import mqtt_nats_bridge as bridge

    created_streams: list[tuple[str, list[str]]] = []

    async def fake_ensure_stream(js, name: str, subjects: list[str]) -> None:
        created_streams.append((name, subjects))

    async def _run_one_tick():
        with patch.object(bridge, "_ensure_stream", side_effect=fake_ensure_stream), \
             patch("nats.connect", new_callable=AsyncMock) as mock_connect, \
             patch("aiomqtt.Client") as mock_mqtt_cls:

            # NATS mock — jetstream() is synchronous; use MagicMock to avoid coroutine
            mock_nc = AsyncMock()
            mock_nc.jetstream = MagicMock(return_value=MagicMock())
            mock_connect.return_value = mock_nc

            # MQTT mock: return an empty async generator so run() exits cleanly
            mock_mqtt = AsyncMock()
            mock_mqtt.__aenter__ = AsyncMock(return_value=mock_mqtt)
            mock_mqtt.__aexit__ = AsyncMock(return_value=False)
            mock_mqtt.subscribe = AsyncMock()
            mock_mqtt.messages = _aiter([])
            mock_mqtt_cls.return_value = mock_mqtt

            await bridge.run()

    asyncio.run(_run_one_tick())

    stream_names = [name for name, _ in created_streams]
    assert "BUILDING_OS_VALIDATED" in stream_names, (
        f"run() must call _ensure_stream with BUILDING_OS_VALIDATED; got {stream_names}"
    )
    validated_subjects = next(
        subjects for name, subjects in created_streams if name == "BUILDING_OS_VALIDATED"
    )
    assert "building-os.validated.telemetry" in validated_subjects


async def _aiter(items):
    for item in items:
        yield item


# ── Cycle 3: validated メッセージが NATS へ publish される ────────────────────

def test_validated_message_is_forwarded_to_nats():
    """MQTT building-os.validated.telemetry を受信したら NATS の同名 subject へ publish する。"""
    import mqtt_nats_bridge as bridge

    published: list[tuple[str, bytes]] = []

    async def _noop_ensure(*_args):
        pass

    async def _run_with_one_validated_message():
        with patch.object(bridge, "_ensure_stream", side_effect=_noop_ensure), \
             patch("nats.connect", new_callable=AsyncMock) as mock_connect, \
             patch("aiomqtt.Client") as mock_mqtt_cls:

            mock_js = MagicMock()
            mock_js.publish = AsyncMock(side_effect=lambda topic, payload: published.append((topic, payload)))
            mock_nc = AsyncMock()
            mock_nc.jetstream = MagicMock(return_value=mock_js)
            mock_nc.drain = AsyncMock()
            mock_connect.return_value = mock_nc

            payload = b'{"point_id":"PT001","value":23.5}'
            fake_msg = MagicMock()
            # str() resolves __str__ on the type, not the instance — use a plain string
            fake_msg.topic = "building-os.validated.telemetry"
            fake_msg.payload = payload

            mock_mqtt = AsyncMock()
            mock_mqtt.__aenter__ = AsyncMock(return_value=mock_mqtt)
            mock_mqtt.__aexit__ = AsyncMock(return_value=False)
            mock_mqtt.subscribe = AsyncMock()
            mock_mqtt.messages = _aiter([fake_msg])
            mock_mqtt_cls.return_value = mock_mqtt

            await bridge.run()

    asyncio.run(_run_with_one_validated_message())

    validated_publishes = [(t, p) for t, p in published if t == "building-os.validated.telemetry"]
    assert len(validated_publishes) == 1, (
        f"Expected 1 publish to building-os.validated.telemetry, got: {published}"
    )
    assert validated_publishes[0][1] == b'{"point_id":"PT001","value":23.5}'
