#!/usr/bin/env python3
"""E10 — 長時間ソーク試験 (endurance soak, #297 follow-up / e2e 統合版).

gRPC GatewayIngress で現実的な点数・周期の持続負荷を数時間かけつつ、以下を同時サンプリングする:

  1. コンテナ RSS（`docker stats`）— Connector Worker / NATS の定常性を #297 と同じ方法論
     （開始1時間平均 vs 終了1時間平均、後半のみの回帰スロープ）で観測する。
  2. .NET runtime メトリクス（#370, 任意 / 既定 OFF）— `--prometheus` を渡したときのみ、
     Prometheus 経由で Connector Worker の GC heap（世代別 + LOH/POH）・GC committed・累積
     allocation・thread 数・working set をサンプリングする。
  3. NATS validated stream の consumer pending（`kpi_sampler.py` のロジックを再利用）。
  4. 各サービスの health probe（NATS/OxiGraph/MinIO/ConnectorWorker/API を直接叩く。#297 と同じ
     く単純な成功率を記録）。
  5. コンテナ再起動回数・OOM 有無（`docker inspect`）。

送信は 1 本の gRPC ストリームを --chunk-seconds 毎に張り直す（#297 は単一 24h ストリームだったが、
本スクリプトは途中で connector-worker が再起動しても計測を継続できるようにするため）。
`--chunk-seconds 0` は「張り直さない」= run 全体を 1 本のストリームで流す指定で、#297 と同条件の
A/B 対照（#370 仮説 (a): 300s 毎の再接続そのものが RSS 増加に効いていないか）を取るためにある。
終了時に `quality_checker.py`（parquet mode）で送信数と lake 永続化数を突き合わせ、loss/duplicate を出す。

**#370 が必要とした計測面**: `docker stats` の RSS は cgroup の memory.current であり、managed heap /
native / GC が commit したまま OS に返していない領域 / page cache が全部混ざった一つの数字なので、
RSS 単独では「warm-up で収束する」のか「本当のリーク」なのかを切り分けられない。そこで runtime
メトリクスを有効にした run では `connector_worker_rss_minus_gc_committed_*`（同一 tick の
コンテナ RSS − GC committed の pairwise 差分）を出す。**RSS が伸びているのにこの差分が横ばいなら
増加分は managed heap、差分も伸びているなら native / page cache / GC が返していない領域**、と読む。
Prometheus を指定しなかった run ではこれらのキーは **1 つも出力しない**（0 や null を書くと
「計測した結果ゼロだった」と読めてしまう）。

ただし引く側 `dotnet.gc.last_collection.memory.committed_size` は名前のとおり **直近の GC 時点の**
スナップショットで、更新頻度は export 間隔ではなく **GC の発生頻度**に律速される（E10 の軽負荷では
コレクション間隔が分単位になり得る）。「同時刻の引き算」ではなく「コンテナ RSS − 直近 GC 時点の
committed」であることを踏まえて読む必要があるので、`gc_collections`（累積 GC 回数）も併せて出し、
差分がどれだけ更新された subtrahend で計算されたかを結果ファイルから判定できるようにしてある。

さらに **欠測を「観測」として記録しない**ための仕掛けが 2 つある:
  - 値は必ず `last_over_time(...[2×サンプリング間隔])` で引く。Prometheus の instant query は既定で
    5 分の lookback があるため、remote-write が詰まった系列でも「最後の値」を平然と返す。窓を明示
    しておけば、パイプラインが止まった区間は値が返らない = その tick にキーが付かない、になる。
  - 起動時に「解決した系列名が **その job について実データを持っているか**」を 1 回プローブし、
    結果を `config.runtime_metric_probe` に残す。名前だけ解決できてデータが 1 件も無い run
    （collector が Prometheus に届いていない等）を、走り終わる前に stderr で知らせる。

なお runtime メトリクスの取得経路が Prometheus 一択なのは、runtime image が
`aspnet:8.0-noble-chiseled`（shell も coreutils も dotnet-counters も無い）で
`docker exec ... cat /proc/1/status` が成立しないため。`OtelSetup.cs` は既に
`.AddRuntimeInstrumentation()` を呼んでいるので、observability profile（Prometheus + otel-collector）
を上げるだけで被測定系のコードは一切変わらない（exporter の宛先が生きるかどうかの差のみ）。

これは #297 が要求する ≥72h・確定閾値版の代替ではなく、e2e/ 評価軸に組み込んだ短時間（既定 4-6h）の
反復版。メモリ増加量はまだ安全域が確定していないため `report`（情報値）として出力し、gate は
再起動ゼロ・OOMゼロ・データ整合・health probe 成功率のみを判定する（`e2e/kpi-thresholds.yaml`
E10_endurance_soak）。

Usage:
  python s19_endurance_soak.py --out results/E10 --duration-hours 4 --rate 6 --points 1865
      [--chunk-seconds 300] [--sample-interval 60]
      [--prometheus http://localhost:9090] [--runtime-job building-os-connector-worker]
      [--runtime-container building-os.connector-worker]
      [--ingress localhost:5051] [--oxigraph http://localhost:7878]
      [--minio-endpoint localhost:9000] [--flush-wait <既定: PARQUET_FLUSH_INTERVAL + 60s>]
      [--containers building-os.connector-worker,building-os.nats,building-os.oxigraph,building-os.api,building-os.minio]
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402
import kpi_sampler as kpis  # noqa: E402

import requests  # noqa: E402

DEFAULT_CONTAINERS = [
    "building-os.connector-worker",
    "building-os.nats",
    "building-os.oxigraph",
    "building-os.minio",
]

# API server is not on the E10 ingest path (gRPC GatewayIngress -> connector-worker -> NATS ->
# Parquet writer bypasses it entirely), so it is intentionally not started/probed for this axis.
DEFAULT_HEALTH_PROBES = {
    "nats": "http://localhost:8222/healthz",
    "oxigraph": "http://localhost:7878/",
    "minio": "http://localhost:9000/minio/health/live",
    "connector-worker": "http://localhost:8081/health/ready",
}

_MEM_UNIT = {"B": 1 / (1024 * 1024), "KIB": 1 / 1024, "KB": 1 / 1024, "MIB": 1.0, "MB": 1.0,
             "GIB": 1024.0, "GB": 1024.0}

# runtime メトリクス（#370）は connector-worker プロセス 1 つを見るので、コンテナごとのループでは
# なくこの固定の接頭辞で出す（OTEL_SERVICE_NAME = Prometheus の job ラベル = このコンテナ）。
# ParquetLakeWriterOptions.FlushInterval のアプリ既定（分）。compose は PARQUET_FLUSH_INTERVAL を
# 空で渡す（= アプリ既定に従う）ので、E10 の実運用条件はこの値になる。
DEFAULT_PARQUET_FLUSH_INTERVAL_MIN = 5
# flush 窓の端に当たっても確実に 1 回 flush を跨ぐための余裕[秒]（writer のアイドルポーリングは最大 20s）。
FLUSH_WAIT_MARGIN_S = 60
# 行数が足りないときに追加で flush 窓を待つ回数の上限。
QUALITY_CHECK_RETRIES = 2

RUNTIME_CONTAINER = "building-os.connector-worker"
DEFAULT_RUNTIME_JOB = "building-os-connector-worker"

_MIB = 1024.0 * 1024.0

# 系列名の候補（**正規表現**、優先順）。決め打ちにできない理由:
# OpenTelemetry.Instrumentation.Runtime は .NET semantic conventions 採用時に
# `process.runtime.dotnet.*` → `dotnet.*` へ改名しており、さらに prometheusremotewrite が
# `.`→`_` 変換と `_bytes`/`_total` の付与を行う。名前を決め打ちすると「全部 null の列」が静かに
# 出来上がり、それが分かるのは数時間走り終わった後になる（計測ハーネスにとって最悪の失敗モード）。
# なので実際に Prometheus が持っている系列名から選ぶ。
#
# 緩い候補（末尾の受け皿）にも `^(dotnet|process_runtime_dotnet)_` を必ず付けてある。付けないと
# 例えば `^.*gc.*alloc.*bytes(_total)?$` が Prometheus 自身の `go_gc_heap_allocs_bytes_total` に
# マッチしてしまい、job セレクタで空になるだけなので「誤解決」と「パイプライン停止」が
# 結果ファイル上で区別できなくなる。
_RUNTIME_METRIC_CANDIDATES: dict[str, list[str]] = {
    # 世代別 heap（LOH/POH を含む）。総和と世代内訳の両方をここから引く。
    "gc_heap_size": [
        r"^dotnet_gc_last_collection_heap_size_bytes$",
        r"^dotnet_gc_heap_size_bytes$",
        r"^process_runtime_dotnet_gc_heap_size_bytes$",
        r"^(dotnet|process_runtime_dotnet)_.*gc.*heap.*size.*bytes$",
    ],
    # GC が OS から commit している総量。RSS との差分が #370 の判別指標になる。
    # 注意: `last_collection` 系は「直近の GC 時点」のスナップショットで、鮮度は export 間隔では
    # なく GC 頻度に律速される。だから下の gc_collections を必ず併せて出す。
    "gc_committed": [
        r"^dotnet_gc_last_collection_memory_committed_size_bytes$",
        r"^dotnet_gc_memory_committed_size_bytes$",
        r"^process_runtime_dotnet_gc_committed_memory_size_bytes$",
        r"^(dotnet|process_runtime_dotnet)_.*gc.*committed.*bytes$",
    ],
    # 累積 allocation（単調増加の counter）。増加率＝アロケーション圧の目安。
    "gc_allocated_total": [
        r"^dotnet_gc_heap_total_allocated_bytes_total$",
        r"^dotnet_gc_heap_total_allocated_bytes$",
        r"^process_runtime_dotnet_gc_allocations_size_bytes_total$",
        r"^(dotnet|process_runtime_dotnet)_.*gc.*alloc.*bytes(_total)?$",
    ],
    # 累積 GC 回数。それ自体が指標というより、gc_committed（= 直近 GC 時点の値）がその run で
    # どれくらいの頻度で更新されていたかを示す**判別指標の品質保証**として出す。GC が数分に 1 回
    # しか起きていない区間では rss_minus_gc_committed の伸びは native 増加ではなく
    # 「subtrahend が固定されていただけ」でも起こり得る。
    "gc_collections": [
        r"^dotnet_gc_collections_total$",
        r"^dotnet_gc_collections$",
        r"^process_runtime_dotnet_gc_collections_count_total$",
    ],
    # スレッド増殖もリークの一形態（#297 の調査項目）。ただし取れるのは **thread pool の**
    # スレッド数だけで、専用スレッド（native リソースが張るもの）はこの系列に含まれない。
    # キー名を thread_pool_* にしてあるのはそのため（`thread_count` と書くと「スレッドは
    # 増えていない」と読み違える）。
    "thread_pool_thread_count": [
        r"^dotnet_thread_pool_thread_count$",
        r"^process_runtime_dotnet_thread_pool_threads_count$",
        r"^(dotnet|process_runtime_dotnet)_.*thread_pool.*thread(s)?_count$",
    ],
    # プロセス側から見た working set（cgroup RSS との突き合わせ用。旧スキームには存在しない）。
    "working_set": [
        r"^dotnet_process_memory_working_set_bytes$",
        r"^process_runtime_dotnet_process_memory_working_set_bytes$",
        r"^(dotnet|process_runtime_dotnet)_.*working_set.*bytes$",
    ],
}

# MiB 換算する概念（回数・個数系は生値のまま出す）。
_RUNTIME_BYTE_CONCEPTS = {"gc_heap_size", "gc_committed", "gc_allocated_total", "working_set"}

# 世代ラベル名も semconv 移行で変わっている（`generation` → `dotnet_gc_heap_generation`）ので、
# こちらも系列が実際に持っているラベルから採る。ラベルの **値**（gen0/LOH/…）も決め打ちしない —
# `sum by (label)` で返ってきたものをそのままキーにする（値が改名されたら名前が変わるだけで、
# 「全部 null の列」にはならない）。
_GC_GENERATION_LABEL_CANDIDATES = [r"^generation$", r"^.*gc.*generation$", r"^.*generation$"]

# 系列名の解決に失敗した / job に実データが無かったときに、この時間だけ毎サンプリング間隔で
# 再解決を試みる。Prometheus は永続ボリューム上の TSDB を WAL リプレイしている間 /api/v1/* に
# 503 を返すので、起動直後の 1 回きりの解決だけだと「数時間走ったのに runtime 列が空」になる。
_RUNTIME_RESOLVE_RETRY_WINDOW_S = 900.0

# runtime サンプリングに使ってよい 1 tick あたりの実時間の上限を決める係数。Prometheus が
# head compaction 等で遅いとき（refuse ではなく「遅く答える」）に、任意計測が RSS サンプリングの
# 周期そのものを崩してはいけない — 崩れると #297/#370 の主計測である RSS 系列の解像度が落ちる。
_RUNTIME_QUERY_BUDGET_RATIO = 0.25
_RUNTIME_QUERY_BUDGET_MIN_S = 5.0
_RUNTIME_QUERY_BUDGET_MAX_S = 20.0

_warned: set[str] = set()


def _warn_once(key: str, message: str) -> None:
    """同じ診断を毎 tick（数時間 = 数百行）出さないための一度きりの警告。"""
    if key not in _warned:
        _warned.add(key)
        print(message, file=sys.stderr)


def _first_matching(candidates: list[str], available: list[str]) -> str | None:
    """優先順の正規表現を順に試し、最初にマッチした候補の中から名前を 1 つ返す（決定的に sorted 先頭）。"""
    for pattern in candidates:
        rx = re.compile(pattern)
        hits = sorted(name for name in available if rx.match(name))
        if hits:
            return hits[0]
    return None


def _prom_json(prom_url: str, path: str, params: dict | None = None) -> list:
    """Prometheus のメタデータ系エンドポイントを叩いて data 配列を返す。失敗は空リスト。"""
    r = requests.get(f"{prom_url.rstrip('/')}{path}", params=params or {}, timeout=8)
    r.raise_for_status()
    data = r.json().get("data", [])
    return data if isinstance(data, list) else []


def resolve_runtime_metric_names(prom_url: str) -> dict[str, str | None]:
    """Prometheus が実際に持っている系列名から runtime メトリクス名を解決する（#370）。

    返すのは各 concept → 系列名（解決できなければ None）と、GC heap の世代ラベル名
    (`gc_heap_generation_label`)。**例外は投げない** — observability profile を上げ忘れた run でも
    ソーク自体は完走しなければならないため、到達不能なら全部 None に縮退する。"""
    names: dict[str, str | None] = {c: None for c in _RUNTIME_METRIC_CANDIDATES}
    names["gc_heap_generation_label"] = None
    if not prom_url:
        return names
    try:
        available = [str(n) for n in _prom_json(prom_url, "/api/v1/label/__name__/values")]
    except (requests.RequestException, ValueError, AttributeError) as e:  # noqa: BLE001
        print(f"[s19][runtime] metric name discovery failed ({type(e).__name__}: {e}) — "
              f"runtime sampling disabled for this run", file=sys.stderr)
        return names
    for concept, candidates in _RUNTIME_METRIC_CANDIDATES.items():
        names[concept] = _first_matching(candidates, available)

    heap = names.get("gc_heap_size")
    if heap:
        try:
            series = _prom_json(prom_url, "/api/v1/series", {"match[]": heap})
        except (requests.RequestException, ValueError, AttributeError):
            series = []
        labels = sorted({k for s in series if isinstance(s, dict) for k in s if k != "__name__"})
        names["gc_heap_generation_label"] = _first_matching(_GC_GENERATION_LABEL_CANDIDATES, labels)

    missing = [c for c in _RUNTIME_METRIC_CANDIDATES if names[c] is None]
    if missing:
        print(f"[s19][runtime] unresolved metrics (not sampled): {','.join(missing)}")
    return names


def runtime_lookback_seconds(sample_interval: int) -> int:
    """`last_over_time` の窓[s]。サンプリング間隔の 2 倍（最低 120s = スクレイプ 30s の 4 倍）。

    Prometheus の instant query は既定で 5 分の lookback を持つので、素で引くと remote-write が
    止まった系列でも最後の値を返し続ける。その値を「新鮮な RSS」と対にして pairwise 差分に入れると、
    subtrahend が凍っているだけの伸びを native 増加と読んでしまう（#370 の結論を誤らせる）。
    窓を明示すれば、欠測区間はキー自体が付かない = 差分計算に混ざらない。"""
    return max(120, int(sample_interval) * 2)


def _runtime_query_budget_seconds(sample_interval: int) -> float:
    return max(_RUNTIME_QUERY_BUDGET_MIN_S,
               min(_RUNTIME_QUERY_BUDGET_MAX_S, float(sample_interval) * _RUNTIME_QUERY_BUDGET_RATIO))


def _fresh_selector(metric: str, job: str, lookback_s: int) -> str:
    return f'last_over_time({metric}{{job="{job}"}}[{lookback_s}s])'


def _sanitize_label_value(value: str) -> str:
    """ラベル値（`gen0` / `LOH` / `Gen 2` …）をメトリクスキーの断片に落とす。"""
    return re.sub(r"[^0-9a-z]+", "_", str(value).lower()).strip("_")


def _prom_vector_by_label(prom_url: str, query: str, label: str,
                           timeout: float = 5.0) -> dict[str, float]:
    """`sum by (<label>) (...)` の結果を {ラベル値: 値} で返す。失敗は空 dict。

    `kpi_sampler.prom_instant` はスカラーしか返せないので、世代内訳だけはここで受ける。
    こうすることで (1) 世代ラベルの **値** を決め打ちせずに済み、(2) 1 tick あたりの
    Prometheus 往復が 世代数ぶん減る（5 クエリ → 1 クエリ）。"""
    try:
        r = requests.get(f"{prom_url.rstrip('/')}/api/v1/query", params={"query": query},
                         timeout=timeout)
        r.raise_for_status()
        result = r.json().get("data", {}).get("result", [])
        out: dict[str, float] = {}
        for series in result:
            key = (series.get("metric") or {}).get(label)
            if key is None:
                continue
            out[str(key)] = float(series["value"][1])
        return out
    except (requests.RequestException, KeyError, ValueError, IndexError, TypeError, AttributeError):
        return {}


def probe_runtime_metrics(prom_url: str, names: dict[str, str | None], job: str,
                           sample_interval: int = 60) -> dict[str, bool]:
    """解決済みの系列名が **その job について実データを持っているか** を 1 回だけ確かめる。

    `/api/v1/label/__name__/values` は保持期間全体の名前を返すので、昨日の run の系列が残っていれば
    collector が Prometheus に届いていない今日の run でも 5 概念すべてが「解決」してしまう。
    その状態は「名前だけ埋まった結果ファイル + 全部 `—` の KPI」になり、走り終わるまで気付けない。
    ここで実データの有無を取っておけば、config に残るし起動直後に警告も出せる。
    返すのは **解決できた concept だけ** の {concept: 実データあり}。"""
    probe: dict[str, bool] = {}
    if not prom_url:
        return probe
    lookback = runtime_lookback_seconds(sample_interval)
    for concept in _RUNTIME_METRIC_CANDIDATES:
        metric = names.get(concept)
        if not metric:
            continue
        probe[concept] = kpis.prom_instant(
            prom_url, f"sum({_fresh_selector(metric, job, lookback)})") is not None
    return probe


def should_retry_runtime_resolution(prom_url: str, probe: dict[str, bool], elapsed_s: float,
                                     since_last_s: float, sample_interval: int) -> bool:
    """系列名の再解決をこの tick で試みるべきか（純関数。呼び出し側でループから使う）。

    「Prometheus 指定あり」かつ「まだ 1 概念も実データが取れていない」かつ「run 開始から
    _RUNTIME_RESOLVE_RETRY_WINDOW_S 以内」かつ「前回の解決から 1 サンプリング間隔以上経っている」
    のときだけ True。1 度でも実データが取れたら二度と再解決しない（被測定系と Prometheus に
    余計な負荷をかけない）。"""
    if not prom_url:
        return False
    if any(probe.values()):
        return False
    if elapsed_s > _RUNTIME_RESOLVE_RETRY_WINDOW_S:
        return False
    return since_last_s >= max(30.0, float(sample_interval))


def sample_runtime(prom_url: str, names: dict[str, str | None], job: str,
                    sample_interval: int = 60) -> dict:
    """解決済みの系列名で 1 tick 分の .NET runtime メトリクスを引く。

    解決できなかった concept はクエリすらせずキーも作らない（欠測を 0 として記録しないため）。
    値は必ず `last_over_time(...[窓])` 経由で引く（runtime_lookback_seconds の docstring 参照）。
    Prometheus が遅いときに RSS サンプリングの周期を崩さないよう、1 tick あたりの実時間予算を
    超えたらそこで打ち切る（任意計測を落として主計測を守る）。
    どの段階で失敗しても例外は外に出さない — サンプラ子プロセスが落ちると run() が health/pending
    KPI を強制 FAIL 扱いにするので、任意計測の失敗でソーク結果を壊してはいけない。"""
    out: dict = {}
    if not prom_url or not names:
        return out
    lookback = runtime_lookback_seconds(sample_interval)
    deadline = time.monotonic() + _runtime_query_budget_seconds(sample_interval)
    try:
        for concept in _RUNTIME_METRIC_CANDIDATES:
            metric = names.get(concept)
            if not metric:
                continue
            if time.monotonic() >= deadline:
                _warn_once("budget", "[s19][runtime] per-tick query budget exhausted — runtime "
                                     "columns will be sparse (Prometheus is answering slowly)")
                return out
            val = kpis.prom_instant(prom_url, f"sum({_fresh_selector(metric, job, lookback)})")
            if val is None:
                continue
            if concept in _RUNTIME_BYTE_CONCEPTS:
                out[f"{concept}_mib"] = round(val / _MIB, 2)
            else:
                out[concept] = round(val, 2)

        heap, gen_label = names.get("gc_heap_size"), names.get("gc_heap_generation_label")
        remaining = deadline - time.monotonic()
        if heap and gen_label and remaining > 0:
            per_gen = _prom_vector_by_label(
                prom_url,
                f"sum by ({gen_label}) ({_fresh_selector(heap, job, lookback)})",
                gen_label, timeout=max(1.0, min(5.0, remaining)))
            for raw, val in per_gen.items():
                key = _sanitize_label_value(raw)
                if key:
                    out[f"gc_heap_{key}_mib"] = round(val / _MIB, 2)
            if not per_gen and "gc_heap_size_mib" in out:
                # 総和は取れているのに世代内訳だけ空 = ラベル名かラベル値の解決がずれている。
                # 黙って落とすと「LOH は測れないもの」と読まれてしまうので必ず一度は知らせる。
                _warn_once("gen", f"[s19][runtime] per-generation breakdown empty for "
                                  f"{heap} by ({gen_label}) — LOH/POH columns will be absent")
    except Exception as e:  # noqa: BLE001 — 任意計測の失敗でソークを止めない
        print(f"[s19][runtime] sample failed ({type(e).__name__}: {e})", file=sys.stderr)
    return out


def _parse_mem_to_mib(text: str) -> float | None:
    """'123.4MiB' / '1.943GiB' -> MiB (float). None if unparsable."""
    m = re.match(r"([\d.]+)\s*([A-Za-z]+)", text.strip())
    if not m:
        return None
    val, unit = float(m.group(1)), m.group(2).upper()
    factor = _MEM_UNIT.get(unit)
    return val * factor if factor is not None else None


def docker_stats(containers: list[str]) -> dict[str, float]:
    """One `docker stats --no-stream` call -> {container: rss_mib} for the ones we care about."""
    try:
        out = subprocess.run(
            ["docker", "stats", "--no-stream", "--format", "{{.Name}}\t{{.MemUsage}}"],
            capture_output=True, text=True, timeout=15, check=True,
        ).stdout
    except (subprocess.SubprocessError, OSError):
        return {}
    want = set(containers)
    result: dict[str, float] = {}
    for line in out.splitlines():
        parts = line.split("\t")
        if len(parts) != 2 or parts[0] not in want:
            continue
        mem_used = parts[1].split("/")[0].strip()
        mib = _parse_mem_to_mib(mem_used)
        if mib is not None:
            result[parts[0]] = round(mib, 2)
    return result


def docker_restart_state(containers: list[str]) -> dict[str, dict]:
    """One `docker inspect` call -> {container: {restart_count, oom_killed}}."""
    try:
        out = subprocess.run(
            ["docker", "inspect", "--format",
             "{{.Name}}|{{.RestartCount}}|{{.State.OOMKilled}}", *containers],
            capture_output=True, text=True, timeout=15, check=False,
        ).stdout
    except (subprocess.SubprocessError, OSError):
        return {}
    result: dict[str, dict] = {}
    for line in out.splitlines():
        bits = line.split("|")
        if len(bits) != 3:
            continue
        name = bits[0].lstrip("/")
        result[name] = {"restart_count": int(bits[1]), "oom_killed": bits[2] == "true"}
    return result


def probe_health(probes: dict[str, str]) -> dict[str, bool]:
    out: dict[str, bool] = {}
    for name, url in probes.items():
        try:
            r = requests.get(url, timeout=5)
            out[name] = r.status_code < 500
        except requests.RequestException:
            out[name] = False
    return out


DEFAULT_SEED_VISIBLE_TIMEOUT_S = 600


def wait_visible_at_scale(pb2, pb2g, target: str, gw: str, pid: str,
                           timeout_s: float = DEFAULT_SEED_VISIBLE_TIMEOUT_S,
                           poll_interval_s: float = 5.0, now=time.monotonic) -> bool:
    """seed 済みポイントが ingress から見えるようになるまで待つ（E10 スケール版）。

    `s10.wait_visible` をそのまま使えない理由が 2 つある。どちらも「小規模軸では踏まないが
    E10 では必ず踏む」たぐいのもの:

    1. **予算が足りない。** s10 の既定は 45s。これは数点〜数十点を seed する E5 向けの値で、
       E10 の既定 1,865 点には全く足りない。`IPointMetadataCache` の cold load は OxiGraph の
       ポイント一括 SPARQL 1 本で、これは点数に対して**二次**で効く（実測: 100→0.25s /
       500→2.15s / 1,000→7.62s / 1,865→23.3s / 3,000→56.6s。アイドル時の値なので、seed 直後の
       OxiGraph がまだ落ち着いていない状態や CPU 競合下ではさらに伸びる）。
    2. **1 回の失敗で run ごと落ちる。** s10 の probe は `stub.StreamTelemetry(..., timeout=60)` を
       裸で呼ぶので、cold load が 60s を超えた瞬間 DEADLINE_EXCEEDED が例外として伝播し、
       数時間の soak が seed 直後に落ちる（そして worker 側では、その頃には .NET の既定
       `HttpClient.Timeout` 100s も絡んでくる）。ここでは RPC 例外を「まだ見えない」として
       扱い、予算が尽きるまで再試行する。

    予算を使い切ったかどうかだけを見るので、`now` はテスト用の注入点。"""
    deadline = now() + timeout_s
    while True:
        try:
            if s10.stream_frames(pb2, pb2g, target, [(gw, pid)]) == 1:
                return True
        except Exception as e:  # noqa: BLE001 — DEADLINE_EXCEEDED 等は「まだ見えない」と同義
            print(f"[s19] seed visibility probe not ready yet ({type(e).__name__})", file=sys.stderr)
        if now() >= deadline:
            return False
        time.sleep(poll_interval_s)


def chunk_length_seconds(configured: int, remaining_s: float) -> int:
    """次に張るストリームの長さ[s]。

    `configured <= 0` は「張り直さない」= 残り時間まるごとを 1 チャンク（#370 の A/B 対照。
    #297 の単一 24h ストリームと同条件にして、300s 毎の再接続自体が RSS 増加に効いているかを見る）。
    常に 1 以上を返す — 0 を返すと ingest_loop が sent=0 のチャンクを CPU 全力で回し続ける。"""
    remaining = max(1, int(remaining_s))
    if configured <= 0:
        return remaining
    return max(1, min(configured, remaining))


def _ack_timeout_seconds(seconds: int) -> float:
    """gRPC Ack 待ちの上限。チャンク長より短くしてはいけない（短いと「サーバは受理済みなのに
    クライアントが timeout」になり、chunk_errors だけが立って A/B の比較が成立しない）。
    既定の 300s チャンクでは従来どおり 330s。単一ストリーム（数時間）では 10% を上乗せする。"""
    return seconds + max(30.0, seconds * 0.1)


PROGRESS_INTERVAL_S = 300.0


async def stream_chunk(pb2, pb2g, target: str, gw: str, points: list[str], rate: float,
                        seconds: int, progress=None,
                        progress_interval: float = PROGRESS_INTERVAL_S) -> tuple[int, int, str | None]:
    """Stream ~rate/s for `seconds` on a fresh gRPC stream. Returns (sent, accepted, error).

    `progress(sent)` is invoked at most every `progress_interval` seconds of wall time while frames
    are being generated. It exists for `--chunk-seconds 0` (one stream for the whole run): without
    it that arm journals a single line, written only when the multi-hour stream completes, so an
    interruption at hour 5 leaves a 0-byte ingest record — strictly less recoverable than the very
    arm it is the control for."""
    import grpc  # type: ignore

    interval = 1.0 / rate if rate > 0 else 0.0
    total = max(1, round(rate * seconds))
    sent = 0

    async def gen():
        nonlocal sent
        last_progress = time.monotonic()
        for i in range(total):
            p = points[i % len(points)]
            yield pb2.TelemetryFrame(gateway_id=gw, point_id=p, value_num=20.0 + (i % 100) / 10.0,
                                     timestamp=datetime.now(timezone.utc).isoformat())
            sent += 1
            if progress is not None and (time.monotonic() - last_progress) >= progress_interval:
                last_progress = time.monotonic()
                progress(sent)
            if interval:
                await asyncio.sleep(interval)

    try:
        async with grpc.aio.insecure_channel(target) as ch:
            ack = await asyncio.wait_for(pb2g.GatewayIngressStub(ch).StreamTelemetry(gen()),
                                          timeout=_ack_timeout_seconds(seconds))
        return sent, int(ack.accepted), None
    except Exception as e:  # noqa: BLE001 — chunk failure must not kill the soak
        return sent, 0, f"{type(e).__name__}: {e}"


async def ingest_loop(pb2, pb2g, args, gw: str, points: list[str], out_dir: str,
                       stop_at: float) -> dict:
    path = os.path.join(out_dir, "ingest-timeseries.jsonl")
    total_sent = total_accepted = 0
    errors = 0
    chunks = 0
    with open(path, "a") as fh:
        def journal(rec: dict) -> None:
            fh.write(json.dumps(rec) + "\n")
            fh.flush()

        while time.monotonic() < stop_at:
            chunk_s = chunk_length_seconds(args.chunk_seconds, stop_at - time.monotonic())
            chunks += 1
            chunk_no = chunks

            def on_progress(sent_so_far: int, _n=chunk_no) -> None:
                journal({"kind": "progress", "ts": datetime.now(timezone.utc).isoformat(),
                         "chunk": _n, "sent": sent_so_far})
                print(f"[s19][ingest] chunk {_n} in flight: sent={sent_so_far}")

            sent, accepted, err = await stream_chunk(pb2, pb2g, args.ingress, gw, points,
                                                       args.rate, chunk_s, progress=on_progress)
            total_sent += sent
            total_accepted += accepted
            if err:
                errors += 1
            journal({"kind": "chunk", "ts": datetime.now(timezone.utc).isoformat(),
                     "chunk": chunk_no, "chunk_seconds": chunk_s,
                     "sent": sent, "accepted": accepted, "error": err})
            print(f"[s19][ingest] sent={sent} accepted={accepted}"
                  f"{' error=' + err if err else ''}")
            if err:
                await asyncio.sleep(5)  # backoff before next chunk on failure
    # chunk_count は #370 A/B の**独立変数の実測値**。`chunk_seconds: 0` は「1 本で流す指定」で
    # あって「1 本で流れた」ではない（途中で 1 度でも失敗すれば張り直している）。設定値だけを
    # 見て 1 本 vs 60 本の比較として読むと誤るので、実際に張った本数を結果に載せる。
    return {"sent": total_sent, "accepted": total_accepted, "chunk_errors": errors,
            "chunk_count": chunks}


def resource_sample_tick(containers: list[str], probes: dict[str, str], prom_url: str = "",
                          runtime_names: dict[str, str | None] | None = None,
                          runtime_job: str = DEFAULT_RUNTIME_JOB,
                          sample_interval: int = 60) -> dict:
    mem = docker_stats(containers)
    restarts = docker_restart_state(containers)
    health = probe_health(probes)
    try:
        pending_total, pending_per = kpis.sample_pending(
            os.environ.get("NATS_MONITOR_URL", "http://localhost:8222"), "VALIDATED")
    except requests.RequestException:
        pending_total, pending_per = -1, {}
    tick = {"mem_mib": mem, "restarts": restarts, "health": health,
            "consumer_pending_total": pending_total, "consumer_pending": pending_per}
    # Prometheus 無効（既定）のときは "runtime" キーを一切作らない — `{}` や null を書くと
    # 「計測した結果、値が無かった」と読めてしまうし、#373 時点の tick 形と差分が出る。
    if prom_url:
        runtime = sample_runtime(prom_url, runtime_names or {}, runtime_job, sample_interval)
        if runtime:
            tick["runtime"] = runtime
    return tick


def resource_role_main(args) -> int:
    """Standalone entry point (spawned as a child process — see run()). Runs the sampling loop
    synchronously in its own process/interpreter so its `docker`/`curl`-equivalent subprocess calls
    never fork() inside a process that also holds a live grpc.aio channel (grpc + fork is unsafe:
    https://github.com/grpc/grpc/blob/master/doc/fork_support.md). Writes resource-timeseries.jsonl
    only; the parent re-reads it and computes the summary after both roles finish."""
    containers = args.containers.split(",")
    path = os.path.join(args.out, "resource-timeseries.jsonl")
    duration_s = args.duration_hours * 3600
    stop_at = time.monotonic() + duration_s
    start = time.monotonic()
    # 系列名の解決は原則 run 開始時の 1 回（毎 tick やると Prometheus に無駄な負荷をかけ、被測定系の
    # ノイズにもなる）。ただし **1 概念も実データが取れていない間だけ**は最初の 15 分間リトライする:
    # Prometheus は永続 TSDB の WAL リプレイ中 503 を返すし、connector-worker の初回 OTLP export が
    # まだ着いていないこともある。ここで諦めると数時間走った run 全部が runtime 列ゼロになる。
    runtime_names = resolve_runtime_metric_names(args.prometheus)
    runtime_probe = probe_runtime_metrics(args.prometheus, runtime_names, args.runtime_job,
                                           args.sample_interval)
    last_resolve_at = time.monotonic()
    if args.prometheus:
        write_runtime_metric_names(args.out, runtime_names, runtime_probe)
        print(f"[s19][runtime] sampling job={args.runtime_job} via {args.prometheus}: "
              f"names={json.dumps(runtime_names)} live={json.dumps(runtime_probe)}")
        if not any(runtime_probe.values()):
            print(f"[s19][runtime] WARNING: no live data for job={args.runtime_job} at t=0 — "
                  f"the metric pipeline (connector-worker → otel-collector → Prometheus) may be "
                  f"down; retrying resolution for the next "
                  f"{int(_RUNTIME_RESOLVE_RETRY_WINDOW_S)}s", file=sys.stderr)
    with open(path, "a") as fh:
        while True:
            now = time.monotonic()
            elapsed = round(now - start, 1)
            if should_retry_runtime_resolution(args.prometheus, runtime_probe, elapsed,
                                                now - last_resolve_at, args.sample_interval):
                last_resolve_at = now
                retry_names = resolve_runtime_metric_names(args.prometheus)
                retry_probe = probe_runtime_metrics(args.prometheus, retry_names, args.runtime_job,
                                                     args.sample_interval)
                if any(retry_probe.values()):
                    runtime_names, runtime_probe = retry_names, retry_probe
                    write_runtime_metric_names(args.out, runtime_names, runtime_probe)
                    print(f"[s19][runtime] resolved on retry at t={elapsed}s: "
                          f"names={json.dumps(runtime_names)} live={json.dumps(runtime_probe)}")
            tick = resource_sample_tick(containers, DEFAULT_HEALTH_PROBES,
                                         prom_url=args.prometheus, runtime_names=runtime_names,
                                         runtime_job=args.runtime_job,
                                         sample_interval=args.sample_interval)
            rec = {"ts": datetime.now(timezone.utc).isoformat(), "elapsed_s": elapsed, **tick}
            fh.write(json.dumps(rec) + "\n")
            fh.flush()
            now = time.monotonic()
            if now >= stop_at:
                break
            sleep_s = min(args.sample_interval, stop_at - now)
            time.sleep(max(1.0, sleep_s))
    return 0


_RUNTIME_NAMES_FILE = "runtime-metric-names.json"


def write_runtime_metric_names(out_dir: str, names: dict, probe: dict | None = None) -> None:
    """解決結果とプローブ結果をサンプラ子プロセスから親へ渡す（親は結果 JSON の config に載せる）。
    「全部 null の列だった」のか「別名で取っていた」のか「名前は合っていたがその job のデータが
    1 件も無かった」のかを結果ファイル単体で判別できるようにするため。
    書けなくてもサンプリングは続行する。"""
    try:
        with open(os.path.join(out_dir, _RUNTIME_NAMES_FILE), "w") as f:
            json.dump({"names": names, "probe": probe or {}}, f, indent=2)
    except OSError as e:
        print(f"[s19][runtime] could not record metric names: {e}", file=sys.stderr)


def read_runtime_metric_names(out_dir: str) -> dict | None:
    path = os.path.join(out_dir, _RUNTIME_NAMES_FILE)
    if not os.path.isfile(path):
        return None
    try:
        with open(path) as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def read_resource_samples(out_dir: str) -> list[dict]:
    path = os.path.join(out_dir, "resource-timeseries.jsonl")
    samples: list[dict] = []
    if not os.path.isfile(path):
        return samples
    with open(path) as fh:
        for line in fh:
            line = line.strip()
            if line:
                samples.append(json.loads(line))
    return samples


def _container_series(samples: list[dict], container: str) -> tuple[list[float], list[float]]:
    xs, ys = [], []
    for s in samples:
        v = s["mem_mib"].get(container)
        if v is not None:
            xs.append(s["elapsed_s"] / 3600.0)
            ys.append(v)
    return xs, ys


def _runtime_series(samples: list[dict], key: str) -> tuple[list[float], list[float]]:
    xs, ys = [], []
    for s in samples:
        v = (s.get("runtime") or {}).get(key)
        if v is not None:
            xs.append(s["elapsed_s"] / 3600.0)
            ys.append(float(v))
    return xs, ys


def _series_stats(xs: list[float], ys: list[float]) -> dict[str, float]:
    """RSS と同じ methodology（開始1時間平均 / 終了1時間平均 / 後半だけの回帰スロープ）。
    xs は時間[h]。系列が何であれ同じ読み方ができるよう、RSS 側の計算とここを揃えてある。

    `samples` / `first_elapsed_h` / `last_elapsed_h` を必ず併せて返すのは、**系列ごとにカバレッジが
    違い得る**ため。スロープは常に「その系列自身の後半」で取るので、例えば remote-write が t=2h
    から健全になった run では RSS のスロープが 2.5–5h、GC committed のスロープが 3.5–5h を指す。
    この 2 つを比べて #370 を判定しろ、と言っている以上、どの窓の値なのかが結果ファイルから
    読めなければならない（`first_hour_avg` も系列が 1h 以降に始まれば末尾 10% への縮退値になる）。"""
    n = len(ys)
    first_hour = [y for x, y in zip(xs, ys) if x <= 1.0] or ys[: max(1, n // 10)]
    last_hour_cut = xs[-1] - 1.0 if xs else 0
    last_hour = [y for x, y in zip(xs, ys) if x >= last_hour_cut] or ys[-max(1, n // 10):]
    return {
        "start": round(ys[0], 2),
        "end": round(ys[-1], 2),
        "max": round(max(ys), 2),
        "first_hour_avg": round(sum(first_hour) / len(first_hour), 2),
        "last_hour_avg": round(sum(last_hour) / len(last_hour), 2),
        "growth_per_hour": round(kpis._slope(xs[n // 2:], ys[n // 2:]), 2),
        "samples": n,
        "first_elapsed_h": round(xs[0], 3) if xs else 0.0,
        "last_elapsed_h": round(xs[-1], 3) if xs else 0.0,
    }


def summarize_runtime(samples: list[dict], container: str = RUNTIME_CONTAINER,
                       job: str = DEFAULT_RUNTIME_JOB) -> dict:
    """#370: runtime メトリクス系列の統計と、RSS − GC committed の判別指標。

    Prometheus 無効の run では 1 キーも返さない（空 dict）。0 や null を返すと gate の表に
    「差分ゼロを観測した」と読める行が並んでしまい、計測していない run と区別が付かなくなる。

    判別指標は「同じプロセスの RSS」と「同じプロセスの GC committed」でなければ意味がない。
    RSS 側は `container`（docker）、GC 側は `job`（Prometheus ラベル）という **別々のノブ**で
    選ばれるので、両者が食い違う呼び出し（`--runtime-job` だけ変えた等）では差分を出さず、
    系列統計の接頭辞も job 由来に落とす — connector_worker_ 接頭辞で別プロセスの値を出すのは、
    無意味な数字に正しそうな名前を付けることになる。"""
    metrics: dict = {}
    prefix = container.replace("building-os.", "").replace("-", "_")
    series_keys = sorted({k for s in samples for k in (s.get("runtime") or {})})
    mismatched = job != DEFAULT_RUNTIME_JOB and container == RUNTIME_CONTAINER
    if mismatched:
        prefix = job.replace("building-os-", "").replace("-", "_")
        if series_keys:
            _warn_once("mismatch",
                       f"[s19][runtime] --runtime-job={job} does not match the sampled container "
                       f"{container}; emitting the runtime series under the '{prefix}_' prefix and "
                       f"skipping rss_minus_gc_committed (subtracting two processes is meaningless)")
    for key in series_keys:
        xs, ys = _runtime_series(samples, key)
        if not ys:
            continue
        for stat, val in _series_stats(xs, ys).items():
            metrics[f"{prefix}_{key}_{stat}"] = val
    if mismatched:
        return metrics

    # コンテナ RSS − GC committed。両方揃った tick だけを pairwise で使う（片方欠測の tick を
    # 0 埋めすると差分がそのまま RSS になり、判別指標として意味を失う）。
    xs, ys, paired_rss = [], [], []
    for s in samples:
        rss = s.get("mem_mib", {}).get(container)
        committed = (s.get("runtime") or {}).get("gc_committed_mib")
        if rss is None or committed is None:
            continue
        xs.append(s["elapsed_s"] / 3600.0)
        ys.append(float(rss) - float(committed))
        paired_rss.append(float(rss))
    if not ys:
        if series_keys:
            _warn_once("nopair",
                       f"[s19][runtime] no tick carried both {container} RSS and gc_committed_mib — "
                       f"rss_minus_gc_committed is not emitted (is {container} in --containers?)")
        return metrics
    stats = _series_stats(xs, ys)
    for stat in ("start", "end", "max"):
        metrics[f"{prefix}_rss_minus_gc_committed_mib_{stat}"] = stats[stat]
    metrics[f"{prefix}_rss_minus_gc_committed_growth_mib_per_hour"] = stats["growth_per_hour"]
    metrics[f"{prefix}_rss_minus_gc_committed_samples"] = stats["samples"]
    metrics[f"{prefix}_rss_minus_gc_committed_first_elapsed_h"] = stats["first_elapsed_h"]
    metrics[f"{prefix}_rss_minus_gc_committed_last_elapsed_h"] = stats["last_elapsed_h"]
    # #370 の比較の両辺を同一 tick 集合に揃えたもの。`{prefix}_rss_growth_mib_per_hour` は
    # RSS 系列全体の後半スロープなので、Prometheus のカバレッジが部分的だと差分側と違う時間帯を
    # 指す。この _paired 版と差分側を比べれば、少なくとも窓は同じであることが保証される。
    n = len(paired_rss)
    metrics[f"{prefix}_rss_growth_mib_per_hour_paired"] = round(
        kpis._slope(xs[n // 2:], paired_rss[n // 2:]), 2)
    return metrics


def summarize_resources(samples: list[dict], containers: list[str],
                         baseline_restarts: dict[str, dict] | None = None,
                         runtime_container: str = RUNTIME_CONTAINER,
                         runtime_job: str = DEFAULT_RUNTIME_JOB) -> dict:
    """baseline_restarts ({container: {restart_count, oom_killed}}) should be captured *before* the
    soak's load starts (docker's RestartCount is cumulative since container creation, so a stack
    that was merely brought up moments earlier can already show a nonzero count unrelated to this
    run) — restart_count_total/oom_count_total below are deltas against that baseline, not raw
    Docker counters. Falls back to the first in-run sample when no baseline is supplied."""
    metrics: dict = {}
    restart_total = 0
    oom_any = False
    baseline_restarts = baseline_restarts or {}
    for c in containers:
        xs, ys = _container_series(samples, c)
        key = c.replace("building-os.", "").replace("-", "_")
        if ys:
            n = len(ys)
            first_hour = [y for x, y in zip(xs, ys) if x <= 1.0] or ys[: max(1, n // 10)]
            last_hour_cut = xs[-1] - 1.0 if xs else 0
            last_hour = [y for x, y in zip(xs, ys) if x >= last_hour_cut] or ys[-max(1, n // 10):]
            half = ys[n // 2:]
            half_x = xs[n // 2:]
            slope_per_hour = kpis._slope(half_x, half)
            metrics[f"{key}_rss_start_mib"] = round(ys[0], 1)
            metrics[f"{key}_rss_end_mib"] = round(ys[-1], 1)
            metrics[f"{key}_rss_max_mib"] = round(max(ys), 1)
            metrics[f"{key}_rss_first_hour_avg_mib"] = round(sum(first_hour) / len(first_hour), 1)
            metrics[f"{key}_rss_last_hour_avg_mib"] = round(sum(last_hour) / len(last_hour), 1)
            metrics[f"{key}_rss_growth_mib_per_hour"] = round(slope_per_hour, 2)
            # RSS 系列のカバレッジ。runtime 系列側の `_samples` と突き合わせて「同じ tick 数を
            # 見ているのか」を判定するため（#370 の比較は両辺のカバレッジが揃って初めて成立する）。
            metrics[f"{key}_rss_samples"] = n
        # restart/OOM delta: last sample that reported this container, minus the pre-soak baseline
        base = baseline_restarts.get(c)
        if base is None:
            for s in samples:
                r = s["restarts"].get(c)
                if r:
                    base = r
                    break
        for s in reversed(samples):
            r = s["restarts"].get(c)
            if r:
                base_count = base["restart_count"] if base else 0
                base_oom = base["oom_killed"] if base else False
                restart_total += max(0, r["restart_count"] - base_count)
                oom_any = oom_any or (r["oom_killed"] and not base_oom)
                break
    health_names = {k for s in samples for k in s["health"]}
    total_probes = ok_probes = 0
    for s in samples:
        for name in health_names:
            if name in s["health"]:
                total_probes += 1
                ok_probes += 1 if s["health"][name] else 0
    metrics["health_probe_success_rate"] = round(ok_probes / total_probes, 4) if total_probes else None
    metrics["restart_count_total"] = restart_total
    metrics["oom_count_total"] = int(oom_any)

    pend = [s["consumer_pending_total"] for s in samples if s["consumer_pending_total"] >= 0]
    if pend:
        pend_x = [s["elapsed_s"] for s in samples if s["consumer_pending_total"] >= 0]
        half = pend[len(pend) // 2:]
        half_x = pend_x[len(pend_x) // 2:]
        slope = kpis._slope(half_x, half)
        metrics["consumer_pending_max"] = max(pend)
        metrics["consumer_pending_last"] = pend[-1]
        metrics["consumer_pending_slope_per_sec"] = round(slope, 4)
        metrics["pending_stable"] = int(slope <= 1.0)
    else:
        # No valid NATS pending sample the whole run (monitoring unreachable throughout) — emit an
        # explicit failing 0 rather than leaving pending_stable absent, so gate.py FAILs the KPI
        # instead of SKIPping it (missing data must not look like a passing run).
        metrics["pending_stable"] = 0
    metrics.update(summarize_runtime(samples, runtime_container, runtime_job))
    metrics["resource_samples"] = len(samples)
    return metrics


def parquet_flush_interval_s() -> int:
    """ParquetLakeWriterWorker の flush 間隔[秒]。

    writer 側は `PARQUET_FLUSH_INTERVAL`（**分**、>0 のときのみ有効）で上書きでき、未指定なら
    `ParquetLakeWriterOptions.FlushInterval` の既定 5 分
    （`DotNet/BuildingOS.Shared/Infrastructure/Telemetry/ParquetLake/ParquetLakeWriterWorker.cs`）。
    ここで **1 分を既定にしてはいけない** — compose は `PARQUET_FLUSH_INTERVAL` を空で渡す
    （`${PARQUET_FLUSH_INTERVAL:-}` = アプリ既定に従う）ので、E10 の実運用条件では 5 分になる。
    """
    raw = os.environ.get("PARQUET_FLUSH_INTERVAL", "").strip()
    try:
        mins = int(raw)
    except ValueError:
        mins = 0
    return (mins if mins > 0 else DEFAULT_PARQUET_FLUSH_INTERVAL_MIN) * 60


def default_flush_wait_s() -> int:
    """突合前に待つ秒数の既定。

    **固定値にしてはいけない。** 以前は 90 秒固定で、writer の flush 間隔（既定 5 分）より短かった。
    そのため送信終了時点でバッファに残っていた最大 1 flush 窓ぶん（E10 の既定レートで約 1,860 行）が
    レイクに落ちる前に `quality_checker.py` が走り、**実損失ゼロの run が `data_loss_ratio` を
    非ゼロで報告する**（#383）。24h run の実測で 0.23%。レートや点数を上げれば gate 閾値 1% を
    超えて偽陽性の失敗になりうる。

    writer は「最大でも FlushInterval ごと」に flush し、アイドル時のポーリングが最大 20 秒なので、
    最後のフレームの後に確実に 1 回 flush させるには interval + 余裕が要る。
    """
    return parquet_flush_interval_s() + FLUSH_WAIT_MARGIN_S


def reconcile_with_lake(run_id: str, building: str, expected: int, minio_endpoint: str,
                        retries: int = QUALITY_CHECK_RETRIES) -> dict | None:
    """quality_checker を走らせ、行数が足りなければ 1 flush 窓ぶん待って**上限回数まで**やり直す。

    待ち時間の導出（`default_flush_wait_s`）だけでは、writer が遅れたり compaction と重なったりした
    ときに取りこぼす。行数が `expected` に届かない間だけ追加で待つことで、flush 間隔を知らなくても
    正しく突合できる — ただし本当に損失がある run で無駄に待たないよう回数で打ち切る。
    """
    best = None
    for attempt in range(retries + 1):
        qc = run_quality_checker(run_id, building, expected, minio_endpoint)
        if qc is not None and (best is None or qc.get("db_row_count", 0) > best.get("db_row_count", 0)):
            best = qc
        rows = (best or {}).get("db_row_count", 0)
        if rows >= expected or attempt == retries:
            return best
        wait = parquet_flush_interval_s()
        print(f"[s19] lake has {rows}/{expected} rows — waiting {wait}s for another flush "
              f"(attempt {attempt + 1}/{retries})", file=sys.stderr)
        time.sleep(wait)
    return best


def run_quality_checker(run_id: str, building: str, expected: int, minio_endpoint: str) -> dict | None:
    perf = os.path.dirname(os.path.abspath(__file__))
    py = os.path.join(perf, ".venv", "bin", "python")
    py = py if os.path.exists(py) else sys.executable
    cmd = [py, os.path.join(perf, "quality_checker.py"),
           "--run-id", run_id, "--building", building, "--expected", str(expected),
           "--mode", "parquet", "--minio-endpoint", minio_endpoint]
    subprocess.run(cmd, check=False, capture_output=True, text=True, timeout=300)
    result_path = os.path.join(perf, "results", run_id, "quality-check-result.json")
    if os.path.isfile(result_path):
        with open(result_path) as f:
            return json.load(f)
    return None


async def run(args) -> int:
    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    run_id = f"soak-{tag}"
    gw, building = f"GW-SOAK-{tag}", run_id
    points = [f"soak-{tag}-{i:05d}" for i in range(args.points)]
    containers = args.containers.split(",")

    os.makedirs(args.out, exist_ok=True)
    seeded: list[str] = []
    try:
        print(f"[s19] seeding {len(points)} points (gw={gw}, building={building})...")
        for p in points:
            s10.insert_point(args.oxigraph, p, gw, building)
            seeded.append(p)
        if not wait_visible_at_scale(pb2, pb2g, args.ingress, gw, points[0], args.seed_visible_timeout):
            print(f"[s19] seeded points not visible within {args.seed_visible_timeout}s — aborting",
                  file=sys.stderr)
            return 2

        if args.prometheus and args.runtime_container not in containers:
            # 判別指標は「同じプロセスの RSS」と「同じプロセスの GC committed」の引き算。RSS 側が
            # サンプリング対象に居なければ差分は出せないので、数時間走る前にここで言う。
            print(f"[s19] WARNING: --runtime-container {args.runtime_container} is not in "
                  f"--containers ({args.containers}) — rss_minus_gc_committed cannot be computed",
                  file=sys.stderr)
        baseline_restarts = docker_restart_state(containers)
        stop_at = time.monotonic() + args.duration_hours * 3600
        print(f"[s19] soak start: {args.duration_hours}h @ ~{args.rate}/s "
              f"({args.points} points), sampling every {args.sample_interval}s")

        # Resource sampling runs in its own OS process (re-invoking this file with --role resource)
        # rather than a thread in this process: its `docker` subprocess calls must not fork() while
        # this process also holds a live grpc.aio channel (fork-after-grpc-threads is unsafe — see
        # resource_role_main's docstring). It shares the same --out dir and writes
        # resource-timeseries.jsonl, which we read back after both finish.
        resource_proc = subprocess.Popen([
            sys.executable, os.path.abspath(__file__), "--role", "resource",
            "--out", args.out, "--duration-hours", str(args.duration_hours),
            "--sample-interval", str(args.sample_interval), "--containers", args.containers,
            # #370 の runtime サンプリングは子プロセス側で行う。ここで渡し忘れると親だけが
            # Prometheus を知っている状態になり、静かに 1 サンプルも取れない run になる。
            "--prometheus", args.prometheus, "--runtime-job", args.runtime_job,
        ])

        ingest_result = await ingest_loop(pb2, pb2g, args, gw, points, args.out, stop_at)
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, resource_proc.wait)
        resource_metrics = summarize_resources(read_resource_samples(args.out), containers,
                                                baseline_restarts, args.runtime_container,
                                                args.runtime_job)
        resource_metrics["resource_sampler_exit_code"] = resource_proc.returncode
        if resource_proc.returncode != 0:
            # Child crashed (or never wrote a full run's worth of samples) — an empty/partial
            # timeseries would otherwise summarize as restart_count_total=0 / health checks absent,
            # which reads as a clean pass. Force the two data-availability-dependent KPIs to an
            # explicit failing value rather than let missing data look like success.
            print(f"[s19] WARNING: resource sampler exited {resource_proc.returncode} — "
                  f"forcing health/pending KPIs to fail (data for this run window is unreliable)",
                  file=sys.stderr)
            resource_metrics["health_probe_success_rate"] = 0.0
            resource_metrics["pending_stable"] = 0

        print(f"[s19] ingest done: sent={ingest_result['sent']} "
              f"accepted={ingest_result['accepted']} chunks={ingest_result['chunk_count']} "
              f"chunk_errors={ingest_result['chunk_errors']}; "
              f"waiting {args.flush_wait}s for flush...")
        await asyncio.sleep(args.flush_wait)

        # Reconcile against `sent` (frames the client actually generated), not `accepted`: a chunk
        # whose gRPC Ack timed out client-side still reports accepted=0 even though the server had
        # already processed most/all of its frames (see resource_role_main's docstring on why
        # resource sampling is isolated from this — the Ack wait, not the transfer, is what times
        # out under sustained chunked load). Using `accepted` here would understate `expected` and
        # let loss_rate mask real gaps once db_count exceeds it (evaluate() clips loss at 0).
        qc = reconcile_with_lake(run_id, building, ingest_result["sent"], args.minio_endpoint)
        if qc is None:
            # Match s15_ingest_throughput.py's behavior: a missing quality-check-result.json means
            # we cannot vouch for data integrity at all. Leaving loss/dup/rows as None here would
            # make gate.py SKIP those KPIs (absent metric), not FAIL — letting a broken
            # reconciliation step look like a passing run. Report an explicit worst case instead.
            print("[s19] quality_checker produced no result — treating as 100% loss", file=sys.stderr)
            loss, dup, invalid, rows = 1.0, 0.0, 0, 0
        else:
            loss = float(qc.get("loss_rate", 0.0))
            dup = float(qc.get("duplicate_rate", 0.0))
            invalid = int(qc.get("schema_invalid_count", 0))
            rows = int(qc.get("db_row_count", 0))

        metrics = {
            **resource_metrics,
            "sent_total": ingest_result["sent"],
            "accepted_total": ingest_result["accepted"],
            "chunk_errors": ingest_result["chunk_errors"],
            "chunk_count": ingest_result["chunk_count"],
            "lake_rows": rows,
            "data_loss_ratio": round(loss, 6),
            "duplicate_rate": round(dup, 6),
            "schema_invalid_count": invalid,
        }
        config = {"duration_hours": args.duration_hours, "rate": args.rate,
                  "points": args.points, "chunk_seconds": args.chunk_seconds,
                  "sample_interval_s": args.sample_interval, "containers": containers,
                  "run_id": run_id}
        if args.prometheus:
            # 何を測ったのかが結果ファイル単体で分かるように、解決済みの系列名と t=0 のプローブ
            # 結果をそのまま残す（#370: 「全部 null の列」/「別名で取っていた」/「名前は合って
            # いたがその job のデータが 1 件も無かった」を後から区別するため）。
            recorded = read_runtime_metric_names(args.out) or {}
            config["prometheus"] = args.prometheus
            config["runtime_job"] = args.runtime_job
            config["runtime_container"] = args.runtime_container
            config["runtime_metric_names"] = recorded.get("names")
            config["runtime_metric_probe"] = recorded.get("probe")
        result = {
            "axis": "E10_endurance_soak",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "config": config,
            "metrics": metrics,
        }
        out_path = os.path.join(args.out, "E10-soak.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)
        print(f"[s19] wrote {out_path}")
        print(json.dumps(metrics, indent=2))
        hard_fail = (
            metrics.get("restart_count_total", 0) > 0
            or metrics.get("oom_count_total", 0) > 0
            or loss > 0.01
            or resource_proc.returncode != 0
        )
        return 1 if hard_fail else 0
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        print(f"[s19] cleaned up {len(seeded)} seeded points")


def main() -> int:
    ap = argparse.ArgumentParser(description="E10 endurance soak (#297 follow-up)")
    ap.add_argument("--role", choices=["full", "resource"], default="full",
                     help="internal: 'resource' is the child-process entry point run() spawns for "
                          "sampling; end users always want the default 'full'")
    ap.add_argument("--out", default="results/E10")
    ap.add_argument("--duration-hours", type=float, default=4.0)
    ap.add_argument("--rate", type=float, default=6.2167,
                     help="target frames/sec (aggregate; default matches #297's 1865pt/300s THX cadence)")
    ap.add_argument("--points", type=int, default=1865, help="distinct points (default: #297 THX scale)")
    ap.add_argument("--chunk-seconds", type=int, default=300,
                     help="gRPC stream re-open cadence; 0 = one single stream for the whole run "
                          "(the #370 A/B control against the 300s re-open cadence)")
    ap.add_argument("--sample-interval", type=int, default=60, help="resource sampling interval (s)")
    ap.add_argument("--prometheus", default=os.environ.get("PROMETHEUS_URL", ""),
                     help="Prometheus base URL for .NET runtime metrics (#370). Empty = disabled "
                          "(default); needs the compose `observability` profile to be up")
    ap.add_argument("--runtime-job", default=DEFAULT_RUNTIME_JOB,
                     help="Prometheus `job` label of the sampled process (= OTEL_SERVICE_NAME)")
    ap.add_argument("--runtime-container", default=RUNTIME_CONTAINER,
                     help="docker container whose RSS is paired with --runtime-job's GC committed "
                          "for the #370 discriminator. Change it together with --runtime-job — "
                          "the two must name the same process or the difference is meaningless")
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--flush-wait", type=int, default=None,
                     help="突合前に flush を待つ秒数。未指定なら PARQUET_FLUSH_INTERVAL（未設定なら "
                          f"アプリ既定 {DEFAULT_PARQUET_FLUSH_INTERVAL_MIN} 分）+ {FLUSH_WAIT_MARGIN_S}s "
                          "から導出する。固定値にすると writer の flush 間隔より短くなり、実損失ゼロの "
                          "run が data_loss_ratio を非ゼロで報告する（#383）")
    ap.add_argument("--seed-visible-timeout", type=float, default=DEFAULT_SEED_VISIBLE_TIMEOUT_S,
                     help="seed したポイントが ingress から見えるまで待つ上限[s]。cold な "
                          "PointMetadataCache のロードは点数に対して二次で効くので、--points を "
                          "既定より大きくするならここも上げる（wait_visible_at_scale の docstring）")
    ap.add_argument("--containers", default=",".join(DEFAULT_CONTAINERS))
    args = ap.parse_args()
    if args.flush_wait is None:
        args.flush_wait = default_flush_wait_s()
    if args.role == "resource":
        os.makedirs(args.out, exist_ok=True)
        return resource_role_main(args)
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
