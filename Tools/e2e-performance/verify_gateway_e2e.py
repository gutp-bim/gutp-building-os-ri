#!/usr/bin/env python3
"""E2E gateway verification — nexus-gateway point_list.csv を正本とする検証ハーネス.

nexus-gateway の point_list.csv を正本として Building OS との整合性を 3 フェーズで検証する:

  Phase 1 — Pointlist API
    GET /gateways/{gateway_id}/pointlist が CSV と一致するか検証:
    - ポイント件数・point_id 一覧の一致
    - 各ポイントの localId / writable / unit
    - BACnet アドレス (deviceIdBacnet / instanceNoBacnet / objectTypeBacnet)
    - ETag フォーマット ("sha256:..." 形式)
    - If-None-Match → 304 の動作
    - ?since={etag} 差分エンドポイント（変化なし → empty diff）

  Phase 2 — gRPC GatewayIngress (--ingress 指定時)
    - CSV の全 point_id で有効フレームを送信 → 全 accepted
    - 未知 point_id → 全 rejected
    - 別ゲートウェイ ID → 全 rejected

  Phase 3 — Control 分類 (--control-check 指定時)
    - writable=false ポイント → 403 (CanWritePointAsync ゲート)
    - writable=true ポイント → 4xx でないこと (200 または 503 gateway offline)

Usage:
  python verify_gateway_e2e.py --csv point_list.csv [options]
  python verify_gateway_e2e.py --csv point_list.csv --skip-seed --ingress localhost:5051
  python verify_gateway_e2e.py --csv point_list.csv --control-check

Options:
  --csv PATH          nexus-gateway point_list.csv のパス (必須)
  --skip-seed         OxiGraph への seed をスキップ (事前に seed 済みの場合)
  --cleanup           終了後に seed したポイントを削除
  --oxigraph URL      OxiGraph SPARQL エンドポイント (default: http://localhost:7878)
  --base-url URL      API Server ベース URL (default: http://localhost:5000)
  --ingress ADDR      gRPC GatewayIngress エンドポイント (例: localhost:5051)
  --frames N          gRPC 各カテゴリのフレーム数 (default: 全 CSV ポイント)
  --control-check     制御 API 分類テストを実行
  --out DIR           結果 JSON 出力ディレクトリ (default: results/gateway-e2e-<timestamp>)
"""

from __future__ import annotations

import argparse
import csv
import importlib.util
import json
import logging
import os
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger("gw-e2e")

SBCO = "https://www.sbco.or.jp/ont/"
REPO_PROTO = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
    "proto", "gateway_ingress.proto",
)
UNKNOWN_GW = "gw-e2e-unknown-999"


# ── CSV ──────────────────────────────────────────────────────────────────────


def load_csv(path: str) -> list[dict]:
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


# ── seed (import from seed_from_csv) ─────────────────────────────────────────

def _import_seed_module():
    seed_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "seed_from_csv.py")
    spec = importlib.util.spec_from_file_location("seed_from_csv", seed_path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ── API helpers ──────────────────────────────────────────────────────────────

def _api_get(url: str, headers: dict | None = None, timeout: int = 15) -> tuple[int, dict | None, dict]:
    req = urllib.request.Request(url, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310
            body = json.loads(resp.read())
            return resp.status, body, dict(resp.headers)
    except urllib.error.HTTPError as e:
        try:
            body = json.loads(e.read())
        except Exception:  # noqa: BLE001
            body = None
        return e.code, body, dict(e.headers)


def _api_post(url: str, data: dict, headers: dict | None = None) -> tuple[int, dict | None]:
    payload = json.dumps(data).encode("utf-8")
    req = urllib.request.Request(
        url, data=payload,
        headers={"Content-Type": "application/json", **(headers or {})},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:  # noqa: S310
            try:
                body = json.loads(resp.read())
            except Exception:  # noqa: BLE001
                body = {}
            return resp.status, body
    except urllib.error.HTTPError as e:
        try:
            body = json.loads(e.read())
        except Exception:  # noqa: BLE001
            body = {}
        return e.code, body


def _api_get_304(url: str, etag: str, extra_headers: dict | None = None) -> int:
    headers = {"If-None-Match": etag, **(extra_headers or {})}
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:  # noqa: S310
            return resp.status
    except urllib.error.HTTPError as e:
        return e.code


# ── Phase 1: Pointlist API ────────────────────────────────────────────────────

def _check(label: str, ok: bool, detail: str = "") -> bool:
    status = "PASS" if ok else "FAIL"
    msg = f"  [{status}] {label}"
    if detail:
        msg += f" — {detail}"
    (logger.info if ok else logger.error)(msg)
    return ok


def run_pointlist_phase(base_url: str, csv_rows: list[dict], gateway_id: str) -> dict:
    logger.info("── Phase 1: Pointlist API ──────────────────────────────────────")
    url = f"{base_url.rstrip('/')}/gateways/{urllib.parse.quote(gateway_id)}/pointlist"
    # X-Gateway-Id は ローカル dev (DISABLE_AUTH=true) では不要だが mTLS モードの互換ヘッダとして常に送る
    status, body, headers = _api_get(url, headers={"X-Gateway-Id": gateway_id})

    findings: list[str] = []
    passes = 0
    failures = 0

    def record(label: str, ok: bool, detail: str = "") -> None:
        nonlocal passes, failures
        if _check(label, ok, detail):
            passes += 1
        else:
            failures += 1
            findings.append(f"FAIL: {label}" + (f" — {detail}" if detail else ""))

    record("HTTP 200", status == 200, f"got {status}")

    if status != 200:
        return {"phase": "pointlist", "passes": passes, "failures": failures, "findings": findings,
                "skipped": ["all checks skipped (non-200 response)"]}

    points_by_id = {p["pointId"]: p for p in body.get("points", [])}
    csv_by_id = {r["point_id"].strip(): r for r in csv_rows if r["gateway_id"].strip() == gateway_id}

    etag = headers.get("ETag", headers.get("etag", ""))
    record("ETag フォーマット (\"sha256:...\")",
           bool(etag) and etag.startswith('"sha256:') and etag.endswith('"'),
           f"ETag={etag!r}")

    record("ポイント件数一致",
           len(points_by_id) == len(csv_by_id),
           f"API={len(points_by_id)} CSV={len(csv_by_id)}")

    missing = [pid for pid in csv_by_id if pid not in points_by_id]
    extra = [pid for pid in points_by_id if pid not in csv_by_id]
    record("CSV の全 point_id が API に存在する", not missing, f"missing={missing}")
    record("API に CSV 外の余分な point がない", not extra, f"extra={extra}")

    field_errors: list[str] = []
    for pid, row in csv_by_id.items():
        if pid not in points_by_id:
            continue
        pt = points_by_id[pid]

        # writable
        csv_writable = row["writable"].strip().lower() == "true"
        api_writable = pt.get("writable")
        if api_writable is not None and api_writable != csv_writable:
            field_errors.append(f"{pid}.writable: api={api_writable} csv={csv_writable}")

        # unit
        csv_unit = row["unit"].strip() or None
        api_unit = pt.get("unit")
        if csv_unit != api_unit:
            field_errors.append(f"{pid}.unit: api={api_unit!r} csv={csv_unit!r}")

        # localId
        csv_local = row["local_id"].strip() or None
        api_local = pt.get("localId")
        if csv_local != api_local:
            field_errors.append(f"{pid}.localId: api={api_local!r} csv={csv_local!r}")

        # BACnet ネイティブアドレス
        has_bacnet = bool(row.get("device_id_bacnet", "").strip())
        native = pt.get("native")
        if has_bacnet:
            if native is None:
                field_errors.append(f"{pid}.native: expected BACnet native, got null")
            else:
                if native.get("protocol") != "bacnet":
                    field_errors.append(f"{pid}.native.protocol: {native.get('protocol')!r} != 'bacnet'")
                if native.get("deviceId") != row["device_id_bacnet"].strip():
                    field_errors.append(
                        f"{pid}.native.deviceId: {native.get('deviceId')!r} != {row['device_id_bacnet'].strip()!r}")
                if native.get("instanceNo") != row["instance_no_bacnet"].strip():
                    field_errors.append(
                        f"{pid}.native.instanceNo: {native.get('instanceNo')!r} != {row['instance_no_bacnet'].strip()!r}")
                if native.get("objectType") != row["object_type_bacnet"].strip():
                    field_errors.append(
                        f"{pid}.native.objectType: {native.get('objectType')!r} != {row['object_type_bacnet'].strip()!r}")
        else:
            if native is not None:
                field_errors.append(f"{pid}.native: expected null for non-BACnet, got {native}")

    record("全ポイントのフィールド一致 (writable/unit/localId/native)", not field_errors,
           "; ".join(field_errors[:5]) + ("..." if len(field_errors) > 5 else ""))

    # 304 チェック
    if etag:
        code_304 = _api_get_304(url, etag, extra_headers={"X-Gateway-Id": gateway_id})
        record("If-None-Match → 304", code_304 == 304, f"got {code_304}")

        # ?since= 差分チェック（変化なし → empty diff）
        diff_url = f"{url}?since={urllib.parse.quote(etag)}"
        diff_status, diff_body, _ = _api_get(diff_url, headers={"X-Gateway-Id": gateway_id})
        if diff_status == 304:
            record("?since={etag} → 変化なし (304)", True)
        elif diff_status == 200 and diff_body:
            no_diff = (
                not diff_body.get("full")
                and diff_body.get("added", []) == []
                and diff_body.get("removed", []) == []
                and diff_body.get("changed", []) == []
            )
            record("?since={etag} → 変化なし (empty diff)", no_diff,
                   f"full={diff_body.get('full')} added={len(diff_body.get('added',[]))} "
                   f"removed={len(diff_body.get('removed',[]))} changed={len(diff_body.get('changed',[]))}")
        else:
            record("?since={etag} → 変化なし", False, f"status={diff_status}")

    return {
        "phase": "pointlist",
        "passes": passes,
        "failures": failures,
        "findings": findings,
        "etag": etag,
        "point_count_api": len(points_by_id),
        "point_count_csv": len(csv_by_id),
    }


# ── Phase 2: gRPC GatewayIngress ─────────────────────────────────────────────

def _load_ingress_stubs():
    from grpc_tools import protoc  # type: ignore

    out = tempfile.mkdtemp(prefix="gw-e2e-proto-")
    proto_dir = os.path.dirname(REPO_PROTO)
    rc = protoc.main([
        "protoc", f"-I{proto_dir}",
        f"--python_out={out}", f"--grpc_python_out={out}", REPO_PROTO,
    ])
    if rc != 0:
        raise RuntimeError(f"protoc failed (rc={rc}) for {REPO_PROTO}")

    def _imp(name, path):
        spec = importlib.util.spec_from_file_location(name, path)
        mod = importlib.util.module_from_spec(spec)
        sys.modules[name] = mod
        spec.loader.exec_module(mod)  # type: ignore
        return mod

    pb2 = _imp("gateway_ingress_pb2", os.path.join(out, "gateway_ingress_pb2.py"))
    pb2_grpc = _imp("gateway_ingress_pb2_grpc", os.path.join(out, "gateway_ingress_pb2_grpc.py"))
    return pb2, pb2_grpc


def _stream_frames(pb2, pb2_grpc, target: str, frames: list[tuple[str, str]]) -> int:
    import grpc  # type: ignore

    now = datetime.now(timezone.utc).isoformat()

    def gen():
        for gw, pid in frames:
            yield pb2.TelemetryFrame(gateway_id=gw, point_id=pid, value=21.5, timestamp=now)

    with grpc.insecure_channel(target) as ch:
        stub = pb2_grpc.GatewayIngressStub(ch)
        ack = stub.StreamTelemetry(gen(), timeout=60)
        return int(ack.accepted)


def _wait_visible(pb2, pb2_grpc, target: str, gw: str, pid: str, timeout_s: float = 45.0) -> bool:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        if _stream_frames(pb2, pb2_grpc, target, [(gw, pid)]) == 1:
            return True
        time.sleep(3)
    return False


def run_ingress_phase(ingress: str, csv_rows: list[dict]) -> dict:
    logger.info("── Phase 2: gRPC GatewayIngress ────────────────────────────────")
    findings: list[str] = []
    passes = 0
    failures = 0

    def record(label: str, ok: bool, detail: str = "") -> None:
        nonlocal passes, failures
        if _check(label, ok, detail):
            passes += 1
        else:
            failures += 1
            findings.append(f"FAIL: {label}" + (f" — {detail}" if detail else ""))

    try:
        pb2, pb2_grpc = _load_ingress_stubs()
    except Exception as e:  # noqa: BLE001
        logger.error("proto compile failed: %s", e)
        return {"phase": "ingress", "passes": 0, "failures": 1,
                "findings": [f"proto compile error: {e}"]}

    # CSV のゲートウェイ ID とポイント ID を取得
    gw_id = csv_rows[0]["gateway_id"].strip()
    valid_pairs = [(r["gateway_id"].strip(), r["point_id"].strip()) for r in csv_rows]
    n = len(valid_pairs)

    # キャッシュに乗るまで待機
    logger.info("waiting for metadata cache to see seeded points (up to 45s)...")
    first_gw, first_pid = valid_pairs[0]
    visible = _wait_visible(pb2, pb2_grpc, ingress, first_gw, first_pid)
    record("seed したポイントがキャッシュに反映された", visible,
           "45s 以内にキャッシュ可視化されなかった" if not visible else "")

    if not visible:
        return {"phase": "ingress", "passes": passes, "failures": failures,
                "findings": findings, "skipped": ["cache not visible, gRPC tests skipped"]}

    # 有効フレーム
    acc_valid = _stream_frames(pb2, pb2_grpc, ingress, valid_pairs)
    record("有効フレーム全件 accepted", acc_valid == n, f"accepted={acc_valid}/{n}")

    # 未知ポイント
    unknown_pairs = [(gw_id, f"e2e-unknown-{i:04d}") for i in range(n)]
    acc_unknown = _stream_frames(pb2, pb2_grpc, ingress, unknown_pairs)
    record("未知 point_id → 全件 rejected", acc_unknown == 0, f"accepted={acc_unknown}/{n}")

    # 別ゲートウェイ ID
    wrong_gw_pairs = [(UNKNOWN_GW, pid) for _, pid in valid_pairs]
    acc_wrong_gw = _stream_frames(pb2, pb2_grpc, ingress, wrong_gw_pairs)
    record("別 gateway_id → 全件 rejected", acc_wrong_gw == 0, f"accepted={acc_wrong_gw}/{n}")

    resolution_rate = acc_valid / n if n else 0.0
    record("point resolution success rate ≥ 99.9%", resolution_rate >= 0.999,
           f"{resolution_rate:.4f}")

    return {
        "phase": "ingress",
        "passes": passes,
        "failures": failures,
        "findings": findings,
        "frames_sent": n,
        "accepted": {"valid": acc_valid, "unknown": acc_unknown, "wrong_gw": acc_wrong_gw},
        "resolution_rate": round(resolution_rate, 5),
    }


# ── Phase 3: Control 分類 ─────────────────────────────────────────────────────

def run_control_phase(base_url: str, csv_rows: list[dict]) -> dict:
    logger.info("── Phase 3: Control 分類 ────────────────────────────────────────")
    findings: list[str] = []
    passes = 0
    failures = 0

    def record(label: str, ok: bool, detail: str = "") -> None:
        nonlocal passes, failures
        if _check(label, ok, detail):
            passes += 1
        else:
            failures += 1
            findings.append(f"FAIL: {label}" + (f" — {detail}" if detail else ""))

    writable_pts = [r["point_id"].strip() for r in csv_rows if r["writable"].strip().lower() == "true"]
    readonly_pts = [r["point_id"].strip() for r in csv_rows if r["writable"].strip().lower() == "false"]

    logger.info("writable=%d readonly=%d", len(writable_pts), len(readonly_pts))

    # writable=false → 403
    readonly_results = []
    for pid in readonly_pts:
        url = f"{base_url.rstrip('/')}/points/{urllib.parse.quote(pid)}/control"
        status, _ = _api_post(url, {"value": 1.0})
        readonly_results.append((pid, status))

    all_403 = all(s == 403 for _, s in readonly_results)
    bad = [(p, s) for p, s in readonly_results if s != 403]
    record("writable=false ポイント → 全件 403", all_403,
           f"不正: {bad[:3]}" if bad else "")

    # writable=true → 4xx でないこと (200 ok または 503 gateway offline)
    writable_results = []
    for pid in writable_pts:
        url = f"{base_url.rstrip('/')}/points/{urllib.parse.quote(pid)}/control"
        status, _ = _api_post(url, {"value": 1.0})
        writable_results.append((pid, status))

    not_4xx = [(p, s) for p, s in writable_results if 400 <= s < 500]
    record("writable=true ポイント → 4xx でない (200 or 503)",
           not not_4xx,
           f"4xx が返った: {not_4xx[:3]}" if not_4xx else "")

    return {
        "phase": "control",
        "passes": passes,
        "failures": failures,
        "findings": findings,
        "readonly_count": len(readonly_pts),
        "writable_count": len(writable_pts),
        "readonly_403_rate": sum(1 for _, s in readonly_results if s == 403) / len(readonly_results) if readonly_results else None,
    }


# ── main ──────────────────────────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser(description="E2E gateway verification (CSV 正本)")
    ap.add_argument("--csv", required=True, help="nexus-gateway の point_list.csv パス")
    ap.add_argument("--skip-seed", action="store_true", help="OxiGraph への seed をスキップ")
    ap.add_argument("--cleanup", action="store_true", help="終了後に seed したポイントを削除")
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--base-url", default=os.environ.get("BASE_URL", "http://localhost:5000"))
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", ""),
                    help="gRPC GatewayIngress アドレス (例: localhost:5051)。空白でスキップ")
    ap.add_argument("--control-check", action="store_true", help="制御 API 分類テストを実行")
    ap.add_argument("--out", default="")
    args = ap.parse_args()

    if not args.out:
        ts = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
        args.out = os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            f"results/gateway-e2e-{ts}"
        )
    os.makedirs(args.out, exist_ok=True)

    csv_rows = load_csv(args.csv)
    gateway_ids = list(dict.fromkeys(r["gateway_id"].strip() for r in csv_rows))
    logger.info("CSV: %d ポイント / gateways=%s", len(csv_rows), gateway_ids)

    seed_mod = _import_seed_module()

    # ── seed ─────────────────────────────────────────────────────────────────
    if not args.skip_seed:
        logger.info("── Seed ─────────────────────────────────────────────────────────")
        update = seed_mod.build_insert(csv_rows)
        seed_mod.sparql_update(args.oxigraph, update)
        logger.info("Seed 完了: %d ポイント → %s", len(csv_rows), args.oxigraph)
        time.sleep(2)  # twin 反映を待つ

    results: dict = {
        "csv": args.csv,
        "gateway_ids": gateway_ids,
        "point_count": len(csv_rows),
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "phases": {},
    }

    total_passes = 0
    total_failures = 0

    # ── Phase 1: Pointlist API ────────────────────────────────────────────────
    for gw_id in gateway_ids:
        gw_rows = [r for r in csv_rows if r["gateway_id"].strip() == gw_id]
        p1 = run_pointlist_phase(args.base_url, gw_rows, gw_id)
        results["phases"][f"pointlist_{gw_id}"] = p1
        total_passes += p1["passes"]
        total_failures += p1["failures"]

    # ── Phase 2: gRPC GatewayIngress ─────────────────────────────────────────
    if args.ingress:
        p2 = run_ingress_phase(args.ingress, csv_rows)
        results["phases"]["ingress"] = p2
        total_passes += p2["passes"]
        total_failures += p2["failures"]
    else:
        logger.info("Phase 2: --ingress 未指定、gRPC 検証をスキップ")
        results["phases"]["ingress"] = {"phase": "ingress", "skipped": True}

    # ── Phase 3: Control 分類 ─────────────────────────────────────────────────
    if args.control_check:
        p3 = run_control_phase(args.base_url, csv_rows)
        results["phases"]["control"] = p3
        total_passes += p3["passes"]
        total_failures += p3["failures"]
    else:
        logger.info("Phase 3: --control-check 未指定、制御テストをスキップ")
        results["phases"]["control"] = {"phase": "control", "skipped": True}

    # ── cleanup ───────────────────────────────────────────────────────────────
    if args.cleanup:
        logger.info("── Cleanup ──────────────────────────────────────────────────────")
        point_ids = [r["point_id"].strip() for r in csv_rows]
        device_ids = list(dict.fromkeys(r["device_id"].strip() for r in csv_rows))
        seed_mod.sparql_update(args.oxigraph, seed_mod.build_delete(point_ids, device_ids))
        logger.info("Cleanup 完了")

    # ── 集計・出力 ────────────────────────────────────────────────────────────
    results["summary"] = {
        "total_passes": total_passes,
        "total_failures": total_failures,
        "verdict": "PASS" if total_failures == 0 else "FAIL",
    }

    out_path = os.path.join(args.out, "gateway-e2e.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, ensure_ascii=False)

    logger.info("")
    logger.info("═" * 60)
    verdict = results["summary"]["verdict"]
    logger.info("  結果: %s  (PASS=%d / FAIL=%d)", verdict, total_passes, total_failures)
    logger.info("  出力: %s", out_path)
    logger.info("═" * 60)

    return 0 if total_failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
