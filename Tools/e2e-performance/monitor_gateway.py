#!/usr/bin/env python3
"""12 時間ゲートウェイ稼働モニタリング — 30 分ごとに metrics を収集して JSON ログに追記.

使い方:
  python monitor_gateway.py                  # 1 回分の収集 + JSON 追記
  python monitor_gateway.py --init           # ベースライン記録（最初の 1 回）
  python monitor_gateway.py --finalize       # 最終レポート生成（HTML 付録更新）
  python monitor_gateway.py --status         # 現在の状態を stdout 出力（loop 用）
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).parent.parent.parent
OUT_DIR = REPO_ROOT / "Tools/e2e-performance/results/monitoring"
LOG_FILE = OUT_DIR / "monitor_log.json"
HTML_FILE = REPO_ROOT / "docs/repository-review.html"

BASE_URL = os.environ.get("BASE_URL", "http://localhost:5000")
NATS_MON = os.environ.get("NATS_MON", "http://localhost:8222")
HEALTH_PORT = os.environ.get("CONNECTOR_HEALTH", "http://localhost:8081")

MONITORING_HOURS = 12
INTERVAL_MIN = 30


# ── helpers ──────────────────────────────────────────────────────────────────

def _http_get(url: str, timeout: int = 8) -> dict | None:
    try:
        req = urllib.request.Request(url, headers={"Accept": "application/json"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read())
    except Exception:
        return None


def _docker_ps() -> list[dict]:
    try:
        r = subprocess.run(
            ["docker", "compose", "-f", str(REPO_ROOT / "docker-compose.oss.yaml"),
             "ps", "--format", "json"],
            capture_output=True, text=True, timeout=15,
        )
        lines = [l for l in r.stdout.strip().splitlines() if l.strip()]
        return [json.loads(l) for l in lines]
    except Exception:
        return []


def _docker_logs_tail(service: str, lines: int = 200) -> str:
    try:
        r = subprocess.run(
            ["docker", "logs", service, "--timestamps", f"--tail={lines}"],
            capture_output=True, text=True, timeout=15,
        )
        return r.stdout + r.stderr
    except Exception:
        return ""


def _nats_streams() -> dict[str, dict]:
    d = _http_get(f"{NATS_MON}/jsz?streams=1") or {}
    result: dict[str, dict] = {}
    for s in (d.get("account_details") or [{}])[0].get("stream_detail", []):
        st = s.get("state", {})
        result[s["name"]] = {
            "messages": st.get("messages", 0),
            "bytes": st.get("bytes", 0),
            "last_ts": st.get("last_ts", ""),
            "last_seq": st.get("last_seq", 0),
        }
    return result


def _minio_count() -> dict:
    try:
        r = subprocess.run(
            ["docker", "exec", "building-os.minio", "sh", "-c",
             "mc alias set local http://localhost:9000 buildingos buildingos123 --quiet 2>/dev/null && "
             "mc ls --recursive local/cold/ 2>/dev/null | wc -l"],
            capture_output=True, text=True, timeout=20,
        )
        count = int(r.stdout.strip().split("\n")[-1])
        # unknown パーティションのみの件数
        r2 = subprocess.run(
            ["docker", "exec", "building-os.minio", "sh", "-c",
             "mc alias set local http://localhost:9000 buildingos buildingos123 --quiet 2>/dev/null && "
             "mc ls --recursive local/cold/building_id=unknown/ 2>/dev/null | wc -l"],
            capture_output=True, text=True, timeout=20,
        )
        unknown = int(r2.stdout.strip().split("\n")[-1])
        return {"total_objects": count, "unknown_objects": unknown}
    except Exception:
        return {"total_objects": -1, "unknown_objects": -1}


def _count_log_events(log: str, since_line: int) -> dict:
    lines = log.splitlines()
    new_lines = lines[since_line:]
    ingress_frames = 0
    ingress_batches = 0
    egress_connects = 0
    egress_disconnects = 0
    errors = 0
    warns = 0

    for line in new_lines:
        if "Ingress stream completed:" in line:
            m = re.search(r"completed: (\d+)", line)
            if m:
                ingress_frames += int(m.group(1))
                ingress_batches += 1
        if "connected (egress)" in line and "disconnected" not in line:
            egress_connects += 1
        if "disconnected (egress)" in line:
            egress_disconnects += 1
        if " error:" in line.lower() or "Error" in line:
            errors += 1
        if " warn:" in line.lower() or "Warn" in line:
            warns += 1

    return {
        "ingress_frames": ingress_frames,
        "ingress_batches": ingress_batches,
        "egress_connects": egress_connects,
        "egress_disconnects": egress_disconnects,
        "errors": errors,
        "warns": warns,
        "new_log_lines": len(new_lines),
    }


def _container_health(ps: list[dict]) -> dict[str, str]:
    result: dict[str, str] = {}
    for c in ps:
        name = c.get("Name", c.get("Service", "?"))
        status = c.get("Status", c.get("Health", "?"))
        result[name] = status
    return result


# ── main collection ───────────────────────────────────────────────────────────

def collect(prev_log: dict | None) -> dict:
    now = datetime.now(timezone.utc)
    ts = now.isoformat()

    # NATS
    nats = _nats_streams()

    # connector-worker log
    cw_log = _docker_logs_tail("building-os.connector-worker", 500)
    cw_lines = cw_log.splitlines()
    prev_cw_lines = prev_log.get("_cw_line_count", 0) if prev_log else 0
    cw_events = _count_log_events(cw_log, max(0, len(cw_lines) - max(len(cw_lines) - prev_cw_lines, 0)))
    # 単純化: 全ログの最末尾から直前チェック以降を見る
    since_n = max(0, len(cw_lines) - (len(cw_lines) - prev_cw_lines)) if prev_log else 0
    cw_events = _count_log_events(cw_log, since_n)

    # gateway-bridge log
    gb_log = _docker_logs_tail("building-os.gateway-bridge", 200)
    gb_lines = gb_log.splitlines()
    prev_gb_lines = prev_log.get("_gb_line_count", 0) if prev_log else 0
    gb_since_n = max(0, len(gb_lines) - (len(gb_lines) - prev_gb_lines)) if prev_log else 0
    gb_events = _count_log_events(gb_log, gb_since_n)

    # MinIO
    minio = _minio_count()

    # System status
    status = _http_get(f"{BASE_URL}/api/system/status") or {}
    services_up = sum(1 for s in status.get("services", []) if s.get("status") == "up")
    services_total = len(status.get("services", []))

    # Connector health
    health_ok = _http_get(f"{HEALTH_PORT}/health/ready") is not None

    # Container health
    ps = _docker_ps()
    containers = _container_health(ps)
    healthy_count = sum(1 for v in containers.values() if "healthy" in v.lower() or "Up" in v)

    # delta NATS validated messages
    prev_validated = (prev_log or {}).get("nats_validated_msgs", 0)
    delta_validated = nats.get("BUILDING_OS_VALIDATED", {}).get("messages", 0) - prev_validated

    record = {
        "ts": ts,
        "ts_jst": datetime.now().strftime("%Y-%m-%d %H:%M JST"),
        "nats_validated_msgs": nats.get("BUILDING_OS_VALIDATED", {}).get("messages", 0),
        "nats_validated_last_ts": nats.get("BUILDING_OS_VALIDATED", {}).get("last_ts", "")[:19],
        "nats_kv_keys": nats.get("KV_telemetry-latest", {}).get("messages", 0),
        "nats_control_msgs": nats.get("BUILDING_OS_CONTROL", {}).get("messages", 0),
        "delta_validated": delta_validated,
        "minio_total_objects": minio["total_objects"],
        "minio_unknown_objects": minio["unknown_objects"],
        "ingress_frames_since_last": cw_events["ingress_frames"],
        "ingress_batches_since_last": cw_events["ingress_batches"],
        "egress_connects": gb_events["egress_connects"],
        "egress_disconnects": gb_events["egress_disconnects"],
        "errors_in_logs": cw_events["errors"] + gb_events["errors"],
        "warns_in_logs": cw_events["warns"] + gb_events["warns"],
        "services_up": services_up,
        "services_total": services_total,
        "connector_health_ok": health_ok,
        "containers_healthy": healthy_count,
        # alerts
        "alerts": [],
        # internal
        "_cw_line_count": len(cw_lines),
        "_gb_line_count": len(gb_lines),
    }

    # alert evaluation
    alerts = []
    if services_up < services_total:
        alerts.append(f"⚠ API services_up={services_up}/{services_total}")
    if delta_validated == 0 and prev_log is not None:
        alerts.append("⚠ NATS VALIDATED メッセージ増加なし (ingress 停止の可能性)")
    if gb_events["egress_disconnects"] > 3:
        alerts.append(f"⚠ egress disconnect 多発: {gb_events['egress_disconnects']} 回")
    if cw_events["errors"] > 0:
        alerts.append(f"⚠ connector-worker ERROR {cw_events['errors']} 件")
    record["alerts"] = alerts

    return record


# ── HTML update ───────────────────────────────────────────────────────────────

def _row_class(rec: dict) -> str:
    if rec.get("alerts"):
        return " style=\"background:#2a1a1a\""
    if rec.get("delta_validated", 0) == 0:
        return " style=\"background:#1e1e10\""
    return ""


def _build_html_table(records: list[dict]) -> str:
    rows = []
    for i, r in enumerate(records):
        alert_cell = "<br>".join(r.get("alerts", [])) or "—"
        alert_color = "var(--warn)" if r.get("alerts") else "var(--ok)"
        rc = _row_class(r)
        rows.append(
            f'<tr{rc}>'
            f'<td>{r["ts_jst"]}</td>'
            f'<td class="ok">{r["services_up"]}/{r["services_total"]}</td>'
            f'<td>{r["nats_validated_msgs"]:,}</td>'
            f'<td>{r.get("delta_validated", "—"):+d}</td>'
            f'<td>{r["nats_kv_keys"]:,}</td>'
            f'<td>{r["ingress_frames_since_last"]:,}</td>'
            f'<td>{r["egress_disconnects"]}</td>'
            f'<td>{r["minio_unknown_objects"]}</td>'
            f'<td style="color:{alert_color}">{alert_cell}</td>'
            f'</tr>'
        )
    return "\n".join(rows)


def update_html(records: list[dict], elapsed_h: float) -> None:
    html = HTML_FILE.read_text(encoding="utf-8")
    anchor = "<!-- monitoring-table-end -->"
    table_html = f"""<!-- monitoring-table-start -->
<h3>D-9. 30 分ごと計測ログ（{records[0]['ts_jst']} 開始）</h3>
<p class="small"><span class="tag fact">事実</span>
30 分周期で取得。経過 {elapsed_h:.1f} h / {len(records)} チェック実施。
監視項目: サービス稼働数 / NATS validated メッセージ増分 / KV hot キー数 / ingress フレーム数 / egress disconnect 回数 / MinIO Parquet ファイル数 / アラート。
</p>
<table>
<tr>
  <th>時刻（JST）</th><th>API services</th><th>VALIDATED msgs</th><th>Δ msgs</th>
  <th>KV keys</th><th>ingress frames</th><th>egress disc.</th>
  <th>Parquet files</th><th>アラート</th>
</tr>
{_build_html_table(records)}
</table>
<!-- monitoring-table-end -->"""

    if "<!-- monitoring-table-start -->" in html:
        html = re.sub(
            r"<!-- monitoring-table-start -->.*?<!-- monitoring-table-end -->",
            table_html,
            html,
            flags=re.DOTALL,
        )
    else:
        # 付録 D のクローズ div の前に挿入
        html = html.replace(
            '<p class="note">\n<strong>nexus-gateway 案A 改修の要点</strong>',
            table_html + '\n<p class="note">\n<strong>nexus-gateway 案A 改修の要点</strong>',
        )
    HTML_FILE.write_text(html, encoding="utf-8")


# ── entrypoint ────────────────────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--init", action="store_true", help="ベースライン記録")
    ap.add_argument("--finalize", action="store_true", help="最終 HTML 更新")
    ap.add_argument("--status", action="store_true", help="現在の状態を stdout 出力")
    args = ap.parse_args()

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    # 既存ログ読み込み
    records: list[dict] = []
    if LOG_FILE.exists():
        records = json.loads(LOG_FILE.read_text())

    prev = records[-1] if records else None

    # 収集
    rec = collect(prev)
    records.append(rec)
    LOG_FILE.write_text(json.dumps(records, indent=2, ensure_ascii=False))

    # 経過時間
    start_ts = datetime.fromisoformat(records[0]["ts"])
    elapsed = (datetime.fromisoformat(rec["ts"]) - start_ts).total_seconds() / 3600

    # HTML 更新
    update_html(records, elapsed)

    # stdout サマリ
    print(f"[{rec['ts_jst']}] check #{len(records):02d} | "
          f"validated={rec['nats_validated_msgs']:,} (Δ{rec.get('delta_validated',0):+d}) | "
          f"ingress_frames={rec['ingress_frames_since_last']} | "
          f"egress_disc={rec['egress_disconnects']} | "
          f"services={rec['services_up']}/{rec['services_total']} | "
          f"elapsed={elapsed:.1f}h")
    if rec["alerts"]:
        for a in rec["alerts"]:
            print(f"  {a}", file=sys.stderr)

    return 0 if not rec["alerts"] else 1


if __name__ == "__main__":
    sys.exit(main())
