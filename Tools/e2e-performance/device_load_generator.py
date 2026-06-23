#!/usr/bin/env python3
"""Device load generator: publishes synthetic telemetry to Mosquitto via MQTT for E2E performance tests."""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import random
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import aiomqtt

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

DEVICE_TYPES = ["hvac", "bacnet", "environmental", "electric", "behavior"]

SCALES: dict[str, dict[str, int]] = {
    "small":  {"devices": 10,   "points": 100},
    "medium": {"devices": 250,  "points": 2500},
    "large":  {"devices": 1000, "points": 10000},
    "stress": {"devices": 5000, "points": 50000},
}

PROFILES: dict[str, dict[str, Any]] = {
    "baseline": {"interval": 60,  "points_per_msg": 5},
    "burst":    {"interval": 10,  "points_per_msg": 5},
    "wide":     {"interval": 60,  "points_per_msg": 75},
    "mixed":    {"interval": 30,  "points_per_msg": 25},
}


@dataclass
class GeneratorConfig:
    scale: str
    profile: str
    duration: int
    run_id: str
    mqtt_host: str = field(default_factory=lambda: os.environ.get("MQTT_HOST", "localhost"))
    mqtt_port: int = field(default_factory=lambda: int(os.environ.get("MQTT_PORT", "1883")))
    # Mosquitto in the OSS stack requires auth (allow_anonymous false). Empty → anonymous.
    mqtt_username: str | None = field(default_factory=lambda: os.environ.get("MQTT_USERNAME") or None)
    mqtt_password: str | None = field(default_factory=lambda: os.environ.get("MQTT_PASSWORD") or None)
    building: str = field(default_factory=lambda: os.environ.get("BUILDING_ID", "PERF-BUILDING"))


def _build_hvac_data() -> dict[str, Any]:
    modes = ["Heat", "Cool", "Fan", "Auto", "Dry"]
    fans = ["Auto", "Low", "Medium", "High"]
    return {
        "mode": random.choice(modes),
        "fan": random.choice(fans),
        "setTemp": round(random.uniform(18.0, 28.0), 1),
        "onOff": random.choice(["on", "off"]),
        "ambientTemp": round(random.uniform(15.0, 35.0), 1),
    }


def _build_environmental_data() -> dict[str, Any]:
    return {
        "temperature": round(random.uniform(18.0, 30.0), 1),
        "humidity": round(random.uniform(30.0, 80.0), 1),
        "co2": random.randint(400, 2000),
    }


def _build_electric_data() -> dict[str, Any]:
    current = round(random.uniform(0.5, 30.0), 2)
    voltage = round(random.uniform(100.0, 240.0), 1)
    return {
        "current": current,
        "voltage": voltage,
        "power": round(current * voltage, 1),
    }


def _build_bacnet_data() -> dict[str, Any]:
    return {
        "objectType": random.randint(0, 20),
        "instanceNo": random.randint(1, 9999),
        "value": round(random.uniform(0.0, 100.0), 2),
    }


def _build_behavior_data() -> dict[str, Any]:
    return {
        "occupancy": random.randint(0, 50),
        "motion": random.choice([True, False]),
    }


DATA_BUILDERS = {
    "hvac":          _build_hvac_data,
    "environmental": _build_environmental_data,
    "electric":      _build_electric_data,
    "bacnet":        _build_bacnet_data,
    "behavior":      _build_behavior_data,
}


def build_payload(
    device_id: str,
    point_id: str,
    device_type: str,
    building: str,
    run_id: str,
) -> dict[str, Any]:
    data = DATA_BUILDERS[device_type]()
    # pick a representative value from data
    numeric_vals = [v for v in data.values() if isinstance(v, (int, float)) and not isinstance(v, bool)]
    value = numeric_vals[0] if numeric_vals else 0.0

    return {
        "id": str(uuid.uuid4()),
        "device_id": device_id,
        "point_id": point_id,
        "building": building,
        "value": value,
        "data": data,
        "datetime": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "name": f"{device_type}-sensor",
        "test_run_id": run_id,
    }


async def run_device(
    client: aiomqtt.Client,
    device_id: str,
    point_ids: list[str],
    device_type: str,
    building: str,
    run_id: str,
    interval: float,
    deadline: float,
    counters: dict[str, int],
) -> None:
    topic = f"building-os.raw.{device_type}"
    while time.monotonic() < deadline:
        for point_id in point_ids:
            if time.monotonic() >= deadline:
                break
            payload = build_payload(device_id, point_id, device_type, building, run_id)
            try:
                await client.publish(topic, payload=json.dumps(payload).encode(), qos=1)
                counters["sent"] += 1
            except Exception as exc:
                logger.warning("Publish error device=%s: %s", device_id, exc)
                counters["errors"] += 1
        await asyncio.sleep(interval)


async def run_load(cfg: GeneratorConfig) -> dict[str, Any]:
    scale_cfg = SCALES[cfg.scale]
    profile_cfg = PROFILES[cfg.profile]
    devices_count = scale_cfg["devices"]
    points_total = scale_cfg["points"]
    interval = float(profile_cfg["interval"])
    points_per_msg = int(profile_cfg["points_per_msg"])

    # assign points_per_device (at least 1)
    points_per_device = max(1, points_total // devices_count)

    device_list: list[tuple[str, list[str], str]] = []
    for i in range(devices_count):
        dtype = DEVICE_TYPES[i % len(DEVICE_TYPES)]
        device_id = f"perf-{dtype}-{cfg.run_id[:8]}-{i:05d}"
        point_ids = [
            f"perf-point-{cfg.run_id[:8]}-{i:05d}-{j:03d}"
            for j in range(points_per_device)
        ]
        # respect points_per_msg: chunk point_ids
        device_list.append((device_id, point_ids[:points_per_msg], dtype))

    counters: dict[str, int] = {"sent": 0, "errors": 0}
    start_time = datetime.now(timezone.utc)
    deadline = time.monotonic() + cfg.duration

    logger.info(
        "Starting load: run_id=%s scale=%s profile=%s duration=%ds devices=%d",
        cfg.run_id, cfg.scale, cfg.profile, cfg.duration, devices_count,
    )

    async with aiomqtt.Client(
        hostname=cfg.mqtt_host, port=cfg.mqtt_port,
        username=cfg.mqtt_username, password=cfg.mqtt_password,
    ) as client:
        tasks = [
            asyncio.create_task(
                run_device(
                    client, device_id, point_ids, dtype,
                    cfg.building, cfg.run_id, interval, deadline, counters,
                )
            )
            for device_id, point_ids, dtype in device_list
        ]
        await asyncio.gather(*tasks, return_exceptions=True)

    end_time = datetime.now(timezone.utc)

    result = {
        "test_run_id": cfg.run_id,
        "scenario": "load_generator",
        "scale": cfg.scale,
        "profile": cfg.profile,
        "duration_seconds": cfg.duration,
        "total_sent": counters["sent"],
        "total_errors": counters["errors"],
        "devices_count": devices_count,
        "start_time": start_time.isoformat(),
        "end_time": end_time.isoformat(),
    }

    out_dir = Path(__file__).parent / "results" / cfg.run_id
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / "load-generator-result.json"
    out_path.write_text(json.dumps(result, indent=2))
    logger.info("Result saved to %s", out_path)
    logger.info("Sent=%d Errors=%d", counters["sent"], counters["errors"])

    return result


def main() -> None:
    parser = argparse.ArgumentParser(description="Device load generator for Building OS E2E performance tests")
    parser.add_argument("--scale",    required=True, choices=list(SCALES.keys()))
    parser.add_argument("--profile",  required=True, choices=list(PROFILES.keys()))
    parser.add_argument("--duration", type=int, default=600, help="Test duration in seconds")
    parser.add_argument("--run-id",   default=None, help="UUID for this test run (auto-generated if omitted)")
    args = parser.parse_args()

    run_id = args.run_id or str(uuid.uuid4())
    cfg = GeneratorConfig(
        scale=args.scale,
        profile=args.profile,
        duration=args.duration,
        run_id=run_id,
    )
    asyncio.run(run_load(cfg))


if __name__ == "__main__":
    main()
