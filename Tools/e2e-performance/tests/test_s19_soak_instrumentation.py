"""
#370 — E10 ソークハーネスのメモリ計測分離（RED phase spec）。

#297 follow-up の 5h ソーク（run `soak-20260823223433`）で Connector Worker RSS が
132.0 → 544.4 MiB 増えた。だが `docker stats` の RSS は cgroup の memory.current であり、
managed heap / native / GC が commit したまま返していない領域 / page cache が全部混ざった
**一つの数字**なので、これだけでは

  (a) gRPC ingress 経路固有の何か（この run は 300s 毎にストリームを張り直していた）
  (b) warm-up（キャッシュ・JIT・GC の commit）で長時間走れば収束する
  (c) 本当のリーク

を切り分けられない。このテストは、切り分けに必要な計測面をハーネス側に固定する。

**このファイルは本番 C# には一切触れない**（被測定系がバイト単位で同一でなければ計測が無意味）。
検証対象は `s19_endurance_soak.py` / `.sh` / `e2e/kpi-thresholds.yaml` のみ。

計測経路は Prometheus 一択である理由: runtime image は `aspnet:8.0-noble-chiseled` で shell も
coreutils も dotnet-counters も無く `docker exec cat /proc/1/status` が成立しない。一方
`OtelSetup.cs` は既に `.AddRuntimeInstrumentation()` を呼んでいるので、observability profile を
上げれば .NET runtime metrics は Prometheus に載る（アプリ側の変更ゼロ）。

キー名の取り決め（ここが唯一の正本 — GREEN phase はこの名前で実装すること）:

  sample_runtime() が返すキー
    gc_heap_size_mib / gc_committed_mib / gc_allocated_total_mib / working_set_mib
    gc_collections / thread_pool_thread_count
    gc_heap_gen0_mib / gc_heap_gen1_mib / gc_heap_gen2_mib / gc_heap_loh_mib / gc_heap_poh_mib
    （世代の断片は Prometheus が返したラベル値そのもの。決め打ちしない）

  summarize_resources() が出すキー（runtime 系列 S ごと。RSS と同じ統計・同じ methodology）
    connector_worker_{S}_start / _end / _max / _first_hour_avg / _last_hour_avg / _growth_per_hour
    connector_worker_{S}_samples / _first_elapsed_h / _last_elapsed_h   ← カバレッジ

  #370 の判別指標（RSS 系列と GC committed 系列の pairwise 差分）
    connector_worker_rss_minus_gc_committed_mib_start
    connector_worker_rss_minus_gc_committed_mib_end
    connector_worker_rss_minus_gc_committed_growth_mib_per_hour
    connector_worker_rss_growth_mib_per_hour_paired   ← 比較の両辺を同一 tick 集合に揃えたもの

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_s19_soak_instrumentation.py -v
"""
from __future__ import annotations

import asyncio
import importlib.util
import json
import sys
import tempfile
import time
import types
from pathlib import Path
from unittest import mock

import pytest

E2E_DIR = Path(__file__).parent.parent
REPO_ROOT = E2E_DIR.parent.parent


def load_s19():
    # s19 は同ディレクトリの s10 / kpi_sampler を sys.path 経由で import するので、
    # importlib で直接ロードしても副作用なく読める（load_ingress_stubs は run() 内でしか呼ばれない）。
    spec = importlib.util.spec_from_file_location(
        "s19_endurance_soak", E2E_DIR / "s19_endurance_soak.py"
    )
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def s19_source() -> str:
    # 明示 UTF-8: s19_endurance_soak.py の docstring は日本語。
    return (E2E_DIR / "s19_endurance_soak.py").read_text(encoding="utf-8")


def s19_shell_source() -> str:
    return (E2E_DIR / "s19_endurance_soak.sh").read_text(encoding="utf-8")


MIB = 1024 * 1024

# OpenTelemetry.Instrumentation.Runtime 1.17.0 が .NET semantic conventions 採用時に付けた名前を
# prometheusremotewrite が変換した形（`.`→`_`、size 系に `_bytes`、counter に `_total`）。
SEMCONV_NAMES = {
    "gc_heap_size": "dotnet_gc_last_collection_heap_size_bytes",
    "gc_committed": "dotnet_gc_last_collection_memory_committed_size_bytes",
    "gc_allocated_total": "dotnet_gc_heap_total_allocated_bytes_total",
    "gc_collections": "dotnet_gc_collections_total",
    "thread_pool_thread_count": "dotnet_thread_pool_thread_count",
    "working_set": "dotnet_process_memory_working_set_bytes",
}

# 同パッケージの旧名（`process.runtime.dotnet.*`）。古い collector / 古いパッケージが同居する
# デプロイでは今もこちらが出る。working_set は旧スキームに存在しない。
LEGACY_NAMES = {
    "gc_heap_size": "process_runtime_dotnet_gc_heap_size_bytes",
    "gc_committed": "process_runtime_dotnet_gc_committed_memory_size_bytes",
    "gc_allocated_total": "process_runtime_dotnet_gc_allocations_size_bytes_total",
    "gc_collections": "process_runtime_dotnet_gc_collections_count_total",
    "thread_pool_thread_count": "process_runtime_dotnet_thread_pool_threads_count",
}

CONCEPTS = ("gc_heap_size", "gc_committed", "gc_allocated_total", "gc_collections",
            "thread_pool_thread_count", "working_set")

JOB = "building-os-connector-worker"


def _prom_response(payload):
    r = mock.Mock()
    r.status_code = 200
    r.raise_for_status.return_value = None
    r.json.return_value = {"status": "success", "data": payload}
    return r


def _fake_prom_get(metric_names: list[str], series: list[dict] | None = None):
    """`requests.get` の side_effect。Prometheus の 2 つの探索エンドポイントだけ答える。

    - /api/v1/label/__name__/values → 存在する系列名の一覧
    - /api/v1/series?match[]=...    → その系列が持つラベル（世代ラベル名の解決用）
    """

    def _get(url, *args, **kwargs):
        if "/api/v1/label/__name__/values" in url:
            return _prom_response(metric_names)
        if "/api/v1/series" in url:
            return _prom_response(series or [])
        raise AssertionError(f"unexpected Prometheus URL: {url}")

    return _get


def _sample(elapsed_h: float, rss_mib: float | None = None, runtime: dict | None = None,
            container: str = "building-os.connector-worker") -> dict:
    """resource-timeseries.jsonl の 1 行と同じ形。runtime を渡したときだけ "runtime" キーが付く
    （Prometheus 無効時に空 dict や null を書かないのが #370 の要件）。"""
    rec = {
        "ts": "2026-08-24T00:00:00+00:00",
        "elapsed_s": elapsed_h * 3600.0,
        "mem_mib": {} if rss_mib is None else {container: rss_mib},
        "restarts": {container: {"restart_count": 0, "oom_killed": False}},
        "health": {"connector-worker": True},
        "consumer_pending_total": 0,
        "consumer_pending": {},
    }
    if runtime is not None:
        rec["runtime"] = runtime
    return rec


# ── 1-4. resolve_runtime_metric_names: 名前は実行時に解決する ──────────────────
#
# 名前を決め打ちすると「全部 null の列」が静かに出来上がる。計測ハーネスにとってこれが最悪の
# 失敗モード（走ったのにデータが無い、が走り終わるまで分からない）なので、実際に Prometheus が
# 持っている系列名から選ぶことを固定する。


def test_resolve_runtime_metric_names_prefers_semantic_convention_names():
    s19 = load_s19()
    available = list(SEMCONV_NAMES.values()) + ["up", "scrape_duration_seconds"]
    with mock.patch.object(s19.requests, "get", side_effect=_fake_prom_get(available)):
        names = s19.resolve_runtime_metric_names("http://prom:9090")
    for concept, expected in SEMCONV_NAMES.items():
        assert names[concept] == expected, f"{concept} must resolve to the advertised semconv name"


def test_resolve_runtime_metric_names_falls_back_to_legacy_process_runtime_names():
    s19 = load_s19()
    available = list(LEGACY_NAMES.values()) + ["up"]
    with mock.patch.object(s19.requests, "get", side_effect=_fake_prom_get(available)):
        names = s19.resolve_runtime_metric_names("http://prom:9090")
    for concept, expected in LEGACY_NAMES.items():
        assert names[concept] == expected, f"{concept} must fall back to the legacy name"
    # 旧スキームに working set は無い。無いものは None であって 0 でも空文字でもない。
    assert names["working_set"] is None


def test_resolve_runtime_metric_names_maps_unknown_concept_to_none_without_raising():
    s19 = load_s19()
    with mock.patch.object(s19.requests, "get", side_effect=_fake_prom_get(["up", "go_goroutines"])):
        names = s19.resolve_runtime_metric_names("http://prom:9090")
    for concept in CONCEPTS:
        assert names[concept] is None, f"{concept} must be None when neither naming scheme is present"


def test_resolve_runtime_metric_names_returns_all_none_when_prometheus_unreachable():
    """observability profile を上げ忘れた run でも soak 自体は完走しなければならない。"""
    s19 = load_s19()
    boom = s19.requests.exceptions.RequestException("connection refused")
    with mock.patch.object(s19.requests, "get", side_effect=boom):
        names = s19.resolve_runtime_metric_names("http://prom:9090")
    assert isinstance(names, dict)
    for concept in CONCEPTS:
        assert names[concept] is None


def test_resolve_runtime_metric_names_reads_generation_label_from_series_not_assumption():
    """世代ラベル名も semconv 移行で変わっている（`generation` → `dotnet_gc_heap_generation`）ので、
    系列が実際に持っているラベルから採る。"""
    s19 = load_s19()
    available = list(SEMCONV_NAMES.values())
    series = [{"__name__": SEMCONV_NAMES["gc_heap_size"],
               "job": "building-os-connector-worker",
               "dotnet_gc_heap_generation": "gen0"}]
    with mock.patch.object(s19.requests, "get", side_effect=_fake_prom_get(available, series)):
        names = s19.resolve_runtime_metric_names("http://prom:9090")
    assert names["gc_heap_generation_label"] == "dotnet_gc_heap_generation"


def test_resolve_runtime_metric_names_does_not_match_foreign_exporters():
    """緩い受け皿パターンが Prometheus 自身の Go ランタイム系列を拾ってはいけない。拾うと
    job セレクタで空になるだけなので、「誤解決」と「パイプライン停止」が結果ファイル上で
    区別できなくなる（どちらも全部 `—`）。"""
    s19 = load_s19()
    foreign = ["go_gc_heap_allocs_bytes_total", "go_memstats_heap_alloc_bytes",
               "go_gc_heap_goal_bytes", "go_threads", "process_resident_memory_bytes",
               "prometheus_tsdb_head_series"]
    with mock.patch.object(s19.requests, "get", side_effect=_fake_prom_get(foreign)):
        names = s19.resolve_runtime_metric_names("http://prom:9090")
    for concept in CONCEPTS:
        assert names[concept] is None, f"{concept} matched a foreign series: {names[concept]}"


# ── 名前が解決できることと、その job のデータが在ることは別 ─────────────────────


def test_probe_runtime_metrics_flags_resolved_names_that_have_no_data_for_the_job():
    """`/api/v1/label/__name__/values` は保持期間全体（15d）の名前を返す。昨日の run の系列が
    残っていれば、collector が Prometheus に届いていない今日の run でも全概念が「解決」する。
    その run は 5-6h 走って runtime 列ゼロで終わるので、t=0 で実データの有無を取っておく。"""
    s19 = load_s19()
    names = dict(SEMCONV_NAMES, gc_heap_generation_label="dotnet_gc_heap_generation")
    live = {SEMCONV_NAMES["gc_committed"]}

    def fake_prom_instant(prom_url, query):  # noqa: ARG001
        return 1.0 if any(m in query for m in live) else None

    with mock.patch.object(s19.kpis, "prom_instant", side_effect=fake_prom_instant):
        probe = s19.probe_runtime_metrics("http://prom:9090", names, JOB)

    assert probe["gc_committed"] is True
    assert probe["gc_heap_size"] is False
    assert set(probe) == set(CONCEPTS), "every resolved concept must be probed"


def test_probe_runtime_metrics_reports_only_resolved_concepts():
    s19 = load_s19()
    names = {c: None for c in CONCEPTS}
    names["working_set"] = SEMCONV_NAMES["working_set"]
    with mock.patch.object(s19.kpis, "prom_instant", return_value=None):
        probe = s19.probe_runtime_metrics("http://prom:9090", names, JOB)
    assert probe == {"working_set": False}


def test_should_retry_runtime_resolution_recovers_from_a_transient_failure_at_t0():
    """Prometheus は永続 TSDB の WAL リプレイ中 /api/v1/* に 503 を返す。t=0 の 1 回きりの解決で
    諦めると、30 秒後には健全になる Prometheus のもとで 6h ぶんの runtime 列が丸ごと空になる。"""
    s19 = load_s19()
    dead = {"gc_committed": False}
    live = {"gc_committed": True}

    # まだ 1 概念も live でない & リトライ窓の中 & 前回解決から 1 間隔以上 → 再試行する
    assert s19.should_retry_runtime_resolution("http://prom:9090", dead, 60.0, 60.0, 60) is True
    # 1 つでも live になったら二度とやらない（Prometheus と被測定系に無駄な負荷をかけない）
    assert s19.should_retry_runtime_resolution("http://prom:9090", live, 60.0, 60.0, 60) is False
    # 窓を過ぎたらやらない（数時間ずっと叩き続けない）
    assert s19.should_retry_runtime_resolution("http://prom:9090", dead, 4000.0, 60.0, 60) is False
    # 前回解決から間もない tick ではやらない
    assert s19.should_retry_runtime_resolution("http://prom:9090", dead, 60.0, 5.0, 60) is False
    # Prometheus 未指定（既定）の run では一切関与しない
    assert s19.should_retry_runtime_resolution("", {}, 60.0, 600.0, 60) is False


def test_resource_role_main_retries_resolution_and_records_the_probe():
    src = s19_source()
    body = src.split("def resource_role_main", 1)[1].split("\ndef ", 1)[0]
    assert "should_retry_runtime_resolution(" in body, (
        "the sampling loop must be able to recover from a Prometheus that was not ready at t=0"
    )
    assert "probe_runtime_metrics(" in body


# ── 5. sample_runtime: bytes → MiB と世代内訳 ─────────────────────────────────


def _scalar_query(s19, metric: str, sample_interval: int = 60, job: str = JOB) -> str:
    lookback = s19.runtime_lookback_seconds(sample_interval)
    return f'sum(last_over_time({metric}{{job="{job}"}}[{lookback}s]))'


def test_sample_runtime_converts_bytes_to_mib_and_breaks_gc_heap_down_per_generation():
    s19 = load_s19()
    names = dict(SEMCONV_NAMES)
    names["gc_heap_generation_label"] = "dotnet_gc_heap_generation"

    heap = names["gc_heap_size"]
    values = {
        _scalar_query(s19, names["gc_committed"]): 200 * MIB,
        _scalar_query(s19, names["gc_allocated_total"]): 4096 * MIB,
        _scalar_query(s19, names["working_set"]): 512 * MIB,
        _scalar_query(s19, names["gc_collections"]): 812.0,
        _scalar_query(s19, names["thread_pool_thread_count"]): 37.0,
        _scalar_query(s19, heap): 150 * MIB,
    }
    per_gen = {"gen0": 8 * MIB, "gen1": 2 * MIB, "gen2": 100 * MIB,
               "loh": 32 * MIB, "poh": 8 * MIB}

    def fake_prom_instant(prom_url, query):
        assert prom_url == "http://prom:9090"
        return values.get(query)

    def fake_vector(prom_url, query, label, timeout=None):  # noqa: ARG001
        # 世代内訳は 1 クエリ（`sum by`）。ラベル名は names 由来でなければならない
        # （決め打ちの `generation` は不可）。値も決め打ちせず、返ってきたものを使う。
        assert label == "dotnet_gc_heap_generation"
        assert query == (f'sum by (dotnet_gc_heap_generation) '
                         f'(last_over_time({heap}{{job="{JOB}"}}'
                         f'[{s19.runtime_lookback_seconds(60)}s]))')
        return per_gen

    with mock.patch.object(s19.kpis, "prom_instant", side_effect=fake_prom_instant), \
         mock.patch.object(s19, "_prom_vector_by_label", side_effect=fake_vector):
        rt = s19.sample_runtime("http://prom:9090", names, JOB)

    assert rt["gc_committed_mib"] == pytest.approx(200.0, abs=0.01)
    assert rt["gc_heap_size_mib"] == pytest.approx(150.0, abs=0.01)
    assert rt["gc_allocated_total_mib"] == pytest.approx(4096.0, abs=0.01)
    assert rt["working_set_mib"] == pytest.approx(512.0, abs=0.01)
    # 回数・個数系はサイズではないので MiB 換算しない（生値のまま）。
    assert rt["gc_collections"] == pytest.approx(812.0, abs=0.01)
    assert rt["thread_pool_thread_count"] == pytest.approx(37.0, abs=0.01)

    assert rt["gc_heap_gen0_mib"] == pytest.approx(8.0, abs=0.01)
    assert rt["gc_heap_gen1_mib"] == pytest.approx(2.0, abs=0.01)
    assert rt["gc_heap_gen2_mib"] == pytest.approx(100.0, abs=0.01)
    # LOH/POH は #297 が名指しで挙げた調査項目。総和に埋もれさせない。
    assert rt["gc_heap_loh_mib"] == pytest.approx(32.0, abs=0.01)
    assert rt["gc_heap_poh_mib"] == pytest.approx(8.0, abs=0.01)


def test_sample_runtime_bounds_staleness_with_last_over_time():
    """Prometheus の instant query は既定で 5 分の lookback を持つので、素で引くと remote-write が
    詰まった系列でも「最後の値」を返し続ける。凍った GC committed と新鮮な RSS を引き算すると
    「native が伸びている」という #370 の誤った結論が作れてしまうので、窓は必ず明示する。"""
    s19 = load_s19()
    names = {c: None for c in CONCEPTS}
    names["gc_committed"] = SEMCONV_NAMES["gc_committed"]
    names["gc_heap_generation_label"] = None
    queries: list[str] = []

    def fake_prom_instant(prom_url, query):  # noqa: ARG001
        queries.append(query)
        return 64 * MIB

    with mock.patch.object(s19.kpis, "prom_instant", side_effect=fake_prom_instant):
        s19.sample_runtime("http://prom:9090", names, JOB, sample_interval=60)

    assert queries, "the resolved concept must be queried"
    for q in queries:
        assert "last_over_time(" in q, f"unbounded instant query would carry stale values: {q}"
        assert "[120s]" in q, f"window must be 2x the 60s sampling interval, got {q}"
    # 間隔を変えれば窓も追随する（欠測判定が常にサンプリング周期基準であること）。
    assert s19.runtime_lookback_seconds(300) == 600
    # スクレイプ 30s / export 15s より短い窓にはしない（健全な系列を欠測扱いしないため）。
    assert s19.runtime_lookback_seconds(5) == 120


def test_sample_runtime_skips_unresolved_concepts():
    s19 = load_s19()
    names = {c: None for c in CONCEPTS}
    names["gc_committed"] = SEMCONV_NAMES["gc_committed"]
    names["gc_heap_generation_label"] = None

    calls: list[str] = []

    def fake_prom_instant(prom_url, query):  # noqa: ARG001
        calls.append(query)
        return 64 * MIB

    with mock.patch.object(s19.kpis, "prom_instant", side_effect=fake_prom_instant):
        rt = s19.sample_runtime("http://prom:9090", names, JOB)

    assert rt == {"gc_committed_mib": pytest.approx(64.0, abs=0.01)}
    assert all(SEMCONV_NAMES["gc_committed"] in q for q in calls), (
        "unresolved concepts must not be queried at all"
    )


def test_sample_runtime_does_not_hardcode_generation_label_values():
    """世代ラベルの **値** も決め打ちしない。パッケージ更新で `gen0`→`generation0` や `LOH` に
    改名されても、決め打ちだと 5 本とも空になり「LOH は測れない」と読まれる（名前を実行時に
    解決している意味が無くなる）。返ってきたラベル値をそのままキーにする。"""
    s19 = load_s19()
    names = dict(SEMCONV_NAMES, gc_heap_generation_label="dotnet_gc_heap_generation")

    with mock.patch.object(s19.kpis, "prom_instant", return_value=None), \
         mock.patch.object(s19, "_prom_vector_by_label",
                            return_value={"generation0": 8 * MIB, "LOH": 32 * MIB,
                                          "Pinned Object Heap": 4 * MIB}):
        rt = s19.sample_runtime("http://prom:9090", names, JOB)

    assert rt["gc_heap_generation0_mib"] == pytest.approx(8.0, abs=0.01)
    assert rt["gc_heap_loh_mib"] == pytest.approx(32.0, abs=0.01)
    assert rt["gc_heap_pinned_object_heap_mib"] == pytest.approx(4.0, abs=0.01)


def test_prom_vector_by_label_parses_one_sum_by_response():
    """世代内訳が 1 クエリで済むこと（世代ごとに instant query を撃つと 1 tick の Prometheus
    往復が 10 回になり、遅い Prometheus のもとで RSS のサンプリング周期そのものが崩れる）。"""
    s19 = load_s19()
    payload = {"resultType": "vector", "result": [
        {"metric": {"dotnet_gc_heap_generation": "gen2"}, "value": [1.0, str(100 * MIB)]},
        {"metric": {"dotnet_gc_heap_generation": "loh"}, "value": [1.0, str(32 * MIB)]},
    ]}
    r = mock.Mock()
    r.raise_for_status.return_value = None
    r.json.return_value = {"status": "success", "data": payload}
    with mock.patch.object(s19.requests, "get", return_value=r) as spy:
        out = s19._prom_vector_by_label("http://prom:9090", "sum by (x) (y)",
                                         "dotnet_gc_heap_generation")
    assert spy.call_count == 1
    assert out == {"gen2": pytest.approx(100 * MIB), "loh": pytest.approx(32 * MIB)}


def test_sample_runtime_stops_querying_once_the_per_tick_budget_is_spent():
    """任意計測（runtime）の遅延で主計測（RSS の 60s 周期）を壊してはいけない。Prometheus が
    head compaction 等で「遅く答える」とき、全概念を撃ち切ると tick が 60s を超える。"""
    s19 = load_s19()
    names = dict(SEMCONV_NAMES, gc_heap_generation_label="dotnet_gc_heap_generation")
    clock = {"t": 0.0}
    calls: list[str] = []

    def fake_prom_instant(prom_url, query):  # noqa: ARG001
        calls.append(query)
        clock["t"] += 8.0  # prom_instant の timeout 上限で毎回張り付く最悪ケース
        return 1.0 * MIB

    fake_time = types.SimpleNamespace(monotonic=lambda: clock["t"])
    with mock.patch.object(s19, "time", fake_time), \
         mock.patch.object(s19.kpis, "prom_instant", side_effect=fake_prom_instant), \
         mock.patch.object(s19, "_prom_vector_by_label", return_value={}):
        rt = s19.sample_runtime("http://prom:9090", names, JOB, sample_interval=60)

    budget = s19._runtime_query_budget_seconds(60)
    assert budget <= 60 * 0.5, "the runtime budget must stay well inside one sampling interval"
    assert len(calls) <= 3, f"budget {budget}s must cut the query loop short, made {len(calls)}"
    # 打ち切っても、それまでに取れた値は捨てない（部分的な列は欠測より有用）。
    assert rt, "values collected before the budget ran out must still be returned"


# ── 6. Prometheus 無効時の tick は完全に従来どおり ────────────────────────────


def test_resource_sample_tick_has_no_runtime_key_when_prometheus_disabled():
    """Prometheus 無効は既定。そのとき tick は #373 時点と 1 バイトも変わってはいけない
    （"runtime": {} や "runtime": null は「計測した結果ゼロ」と読めてしまう）。"""
    s19 = load_s19()
    containers = ["building-os.connector-worker"]
    probes = {"connector-worker": "http://localhost:8081/health/ready"}

    with mock.patch.object(s19, "docker_stats", return_value={"building-os.connector-worker": 132.0}), \
         mock.patch.object(s19, "docker_restart_state",
                            return_value={"building-os.connector-worker":
                                          {"restart_count": 0, "oom_killed": False}}), \
         mock.patch.object(s19, "probe_health", return_value={"connector-worker": True}), \
         mock.patch.object(s19.kpis, "sample_pending", return_value=(7, {"VALIDATED/w": 7})), \
         mock.patch.object(s19, "sample_runtime") as spy_runtime:
        tick = s19.resource_sample_tick(containers, probes, prom_url="")

    assert "runtime" not in tick
    spy_runtime.assert_not_called()
    assert set(tick) == {"mem_mib", "restarts", "health",
                         "consumer_pending_total", "consumer_pending"}
    assert tick["mem_mib"] == {"building-os.connector-worker": 132.0}
    assert tick["consumer_pending_total"] == 7


def test_resource_sample_tick_includes_runtime_when_prometheus_enabled():
    s19 = load_s19()
    containers = ["building-os.connector-worker"]
    probes = {"connector-worker": "http://localhost:8081/health/ready"}
    names = dict(SEMCONV_NAMES, gc_heap_generation_label="dotnet_gc_heap_generation")

    with mock.patch.object(s19, "docker_stats", return_value={"building-os.connector-worker": 132.0}), \
         mock.patch.object(s19, "docker_restart_state", return_value={}), \
         mock.patch.object(s19, "probe_health", return_value={"connector-worker": True}), \
         mock.patch.object(s19.kpis, "sample_pending", return_value=(0, {})), \
         mock.patch.object(s19, "sample_runtime",
                            return_value={"gc_committed_mib": 90.0}) as spy_runtime:
        tick = s19.resource_sample_tick(containers, probes, prom_url="http://prom:9090",
                                         runtime_names=names, runtime_job=JOB,
                                         sample_interval=60)

    assert tick["runtime"] == {"gc_committed_mib": 90.0}
    # サンプリング間隔は last_over_time の窓（欠測判定）と 1 tick の問い合わせ予算を決めるので、
    # tick からそのまま渡らなければならない。
    spy_runtime.assert_called_once_with("http://prom:9090", names, JOB, 60)


# ── 7-9. summarize_resources: RSS と同じ methodology + #370 の判別指標 ────────


def test_summarize_resources_computes_rss_methodology_for_runtime_series():
    """既存 RSS と同じ統計（start/end/max/開始1時間平均/終了1時間平均/後半だけの回帰スロープ）を
    runtime 系列にも出す。系列は 0,1,2,3h で 100,110,120,130 MiB。後半 = 2h,3h → 10.0 MiB/h。"""
    s19 = load_s19()
    samples = [
        _sample(0.0, rss_mib=132.0, runtime={"gc_heap_size_mib": 100.0}),
        _sample(1.0, rss_mib=200.0, runtime={"gc_heap_size_mib": 110.0}),
        _sample(2.0, rss_mib=300.0, runtime={"gc_heap_size_mib": 120.0}),
        _sample(3.0, rss_mib=400.0, runtime={"gc_heap_size_mib": 130.0}),
    ]
    m = s19.summarize_resources(samples, ["building-os.connector-worker"])

    assert m["connector_worker_gc_heap_size_mib_start"] == pytest.approx(100.0, abs=0.05)
    assert m["connector_worker_gc_heap_size_mib_end"] == pytest.approx(130.0, abs=0.05)
    assert m["connector_worker_gc_heap_size_mib_max"] == pytest.approx(130.0, abs=0.05)
    assert m["connector_worker_gc_heap_size_mib_first_hour_avg"] == pytest.approx(105.0, abs=0.05)
    assert m["connector_worker_gc_heap_size_mib_last_hour_avg"] == pytest.approx(125.0, abs=0.05)
    assert m["connector_worker_gc_heap_size_mib_growth_per_hour"] == pytest.approx(10.0, abs=0.05)
    # 既存 RSS キーは温存されていること（回帰の当たり判定）。
    assert m["connector_worker_rss_growth_mib_per_hour"] == pytest.approx(100.0, abs=0.05)


def test_summarize_resources_emits_nothing_for_discriminator_without_runtime_data():
    """Prometheus を上げずに回した run の結果ファイルに、0 や null の
    rss_minus_gc_committed が載ってはいけない（「差分がゼロだった」と読めてしまう）。

    「無い」だけを見ると未実装のうちは自明に通ってしまうので、同じ入力の runtime 有り/無しを
    対にして「有れば出る・無ければ 1 キーも出ない」を 1 本で押さえる。"""
    s19 = load_s19()
    container = "building-os.connector-worker"

    with_runtime = [
        _sample(0.0, rss_mib=132.0, runtime={"gc_committed_mib": 60.0}),
        _sample(1.0, rss_mib=300.0, runtime={"gc_committed_mib": 60.0}),
    ]
    without_runtime = [_sample(0.0, rss_mib=132.0), _sample(1.0, rss_mib=300.0)]

    on = s19.summarize_resources(with_runtime, [container])
    off = s19.summarize_resources(without_runtime, [container])

    assert "connector_worker_rss_minus_gc_committed_mib_start" in on, (
        "the discriminator must be emitted whenever both RSS and GC-committed samples exist"
    )
    assert "connector_worker_gc_committed_mib_end" in on

    leaked = [k for k in off if "rss_minus_gc_committed" in k or k.startswith("connector_worker_gc_")]
    assert leaked == [], f"runtime-derived keys must be absent entirely, got {leaked}"
    # RSS 側は runtime の有無に関係なく従来どおり出る。
    assert off["connector_worker_rss_end_mib"] == pytest.approx(300.0, abs=0.05)


def test_summarize_resources_discriminates_managed_growth_from_native_growth():
    """#370 が欲しかった唯一の比較。RSS は伸びているが GC committed は横ばい、という run では
    「managed heap の伸び ≒ 0」かつ「RSS − GC committed が大きく伸びている」= 増加分は
    native / page cache / GC が commit したまま返していない領域、と読める。

    RSS 100→250 MiB（50 MiB/h）、GC committed 60 MiB 固定 → 差分 40→190（50 MiB/h）。"""
    s19 = load_s19()
    samples = [
        _sample(0.0, rss_mib=100.0, runtime={"gc_committed_mib": 60.0}),
        _sample(1.0, rss_mib=150.0, runtime={"gc_committed_mib": 60.0}),
        _sample(2.0, rss_mib=200.0, runtime={"gc_committed_mib": 60.0}),
        _sample(3.0, rss_mib=250.0, runtime={"gc_committed_mib": 60.0}),
    ]
    m = s19.summarize_resources(samples, ["building-os.connector-worker"])

    assert m["connector_worker_gc_committed_mib_growth_per_hour"] == pytest.approx(0.0, abs=0.05)
    assert m["connector_worker_rss_minus_gc_committed_mib_start"] == pytest.approx(40.0, abs=0.05)
    assert m["connector_worker_rss_minus_gc_committed_mib_end"] == pytest.approx(190.0, abs=0.05)
    assert m["connector_worker_rss_minus_gc_committed_growth_mib_per_hour"] == pytest.approx(
        50.0, abs=0.05
    )


def test_summarize_resources_discriminator_uses_only_ticks_having_both_inputs():
    """RSS だけ / runtime だけ の tick を混ぜても差分系列は壊れない（pairwise であること）。"""
    s19 = load_s19()
    samples = [
        _sample(0.0, rss_mib=100.0, runtime={"gc_committed_mib": 60.0}),
        _sample(0.5, rss_mib=None, runtime={"gc_committed_mib": 60.0}),   # docker stats 欠測
        _sample(1.0, rss_mib=150.0),                                       # Prometheus 欠測
        _sample(2.0, rss_mib=200.0, runtime={"gc_committed_mib": 60.0}),
    ]
    m = s19.summarize_resources(samples, ["building-os.connector-worker"])
    assert m["connector_worker_rss_minus_gc_committed_mib_start"] == pytest.approx(40.0, abs=0.05)
    assert m["connector_worker_rss_minus_gc_committed_mib_end"] == pytest.approx(140.0, abs=0.05)


def test_summarize_resources_reports_per_series_coverage_so_windows_can_be_compared():
    """スロープは常に「その系列自身の後半」で取るので、Prometheus のカバレッジが部分的だと
    RSS スロープ（0-4h の後半 = 2-4h）と差分スロープ（2-4h の後半 = 3-4h）が違う時間帯を指す。
    #370 はその 2 つを比べて結論を出せと言っているので、どの窓の値なのかが結果ファイルから
    読めなければならない。"""
    s19 = load_s19()
    container = "building-os.connector-worker"
    # RSS は 0-4h 全部、GC committed は 2h 以降だけ（remote-write が遅れて健全化した run）。
    samples = [
        _sample(0.0, rss_mib=100.0),
        _sample(1.0, rss_mib=150.0),
        _sample(2.0, rss_mib=200.0, runtime={"gc_committed_mib": 60.0}),
        _sample(3.0, rss_mib=250.0, runtime={"gc_committed_mib": 60.0}),
        _sample(4.0, rss_mib=300.0, runtime={"gc_committed_mib": 60.0}),
    ]
    m = s19.summarize_resources(samples, [container])

    assert m["connector_worker_rss_samples"] == 5
    assert m["connector_worker_gc_committed_mib_samples"] == 3
    assert m["connector_worker_gc_committed_mib_first_elapsed_h"] == pytest.approx(2.0, abs=0.01)
    assert m["connector_worker_gc_committed_mib_last_elapsed_h"] == pytest.approx(4.0, abs=0.01)
    assert m["connector_worker_rss_minus_gc_committed_samples"] == 3
    assert m["connector_worker_rss_minus_gc_committed_first_elapsed_h"] == pytest.approx(2.0, abs=0.01)


def test_summarize_resources_emits_an_rss_slope_over_the_paired_ticks_only():
    """#370 の比較は「RSS のスロープ」対「差分のスロープ」。両辺が同じ tick 集合から来ていることを
    保証できるように、差分と同一の tick だけで測った RSS スロープを併せて出す。

    RSS は 0-4h で 100→300（一定 50 MiB/h だが後半だけの回帰も 50）。差分側は 2-4h しか無いので、
    _paired も 2-4h の後半（3h,4h）= 50 MiB/h になる。"""
    s19 = load_s19()
    container = "building-os.connector-worker"
    samples = [
        _sample(0.0, rss_mib=100.0),
        _sample(1.0, rss_mib=150.0),
        _sample(2.0, rss_mib=200.0, runtime={"gc_committed_mib": 60.0}),
        _sample(3.0, rss_mib=250.0, runtime={"gc_committed_mib": 60.0}),
        _sample(4.0, rss_mib=300.0, runtime={"gc_committed_mib": 60.0}),
    ]
    m = s19.summarize_resources(samples, [container])
    assert m["connector_worker_rss_growth_mib_per_hour_paired"] == pytest.approx(50.0, abs=0.05)
    # Prometheus 無しの run にはこのキーも出ない（計測していないものを 0 と書かない）。
    off = s19.summarize_resources([_sample(0.0, rss_mib=100.0), _sample(1.0, rss_mib=300.0)],
                                   [container])
    assert "connector_worker_rss_growth_mib_per_hour_paired" not in off


def test_summarize_resources_refuses_to_subtract_two_different_processes():
    """RSS 側は docker のコンテナ名、GC 側は Prometheus の job ラベルという **別々のノブ**で
    選ばれる。`--runtime-job` だけを別プロセスに向けた呼び出しで差分を出すと、意味のない引き算に
    「connector_worker を測った値」という正しそうな名前が付いてしまう。"""
    s19 = load_s19()
    samples = [
        _sample(0.0, rss_mib=100.0, runtime={"gc_committed_mib": 60.0}),
        _sample(1.0, rss_mib=200.0, runtime={"gc_committed_mib": 60.0}),
    ]
    m = s19.summarize_resources(samples, ["building-os.connector-worker"],
                                 runtime_job="building-os-api")

    assert not [k for k in m if "rss_minus_gc_committed" in k], (
        "the discriminator must not be emitted when RSS and GC committed name different processes"
    )
    # runtime 系列自体は残すが、接頭辞は実際に測った job のもの（connector_worker_ は誤読を招く）。
    assert "api_gc_committed_mib_end" in m
    assert "connector_worker_gc_committed_mib_end" not in m
    # RSS 側は従来どおり出る。
    assert m["connector_worker_rss_end_mib"] == pytest.approx(200.0, abs=0.05)


# ── 10. --chunk-seconds 0 = 単一ストリーム（#370 acceptance criterion 3 の A/B 対照）──


def test_chunk_length_seconds_caps_at_configured_cadence():
    s19 = load_s19()
    assert s19.chunk_length_seconds(300, 5 * 3600.0) == 300


def test_chunk_length_seconds_shrinks_to_remaining_time():
    s19 = load_s19()
    assert s19.chunk_length_seconds(300, 100.0) == 100


def test_chunk_length_seconds_zero_means_one_stream_for_the_whole_run():
    """`--chunk-seconds 0` は「張り直さない」= 残り全部を 1 チャンク。#297 の単一 24h ストリームと
    同条件にして、300s 再接続が RSS 増加の一因か（仮説 a）を A/B で切る。"""
    s19 = load_s19()
    assert s19.chunk_length_seconds(0, 5 * 3600.0) == 18000


def test_chunk_length_seconds_never_returns_zero_or_negative():
    """0 を返すと ingest_loop が sent=0 のチャンクを CPU 全力で回し続ける（旧
    `int(min(args.chunk_seconds, ...))` はまさにこれ）。"""
    s19 = load_s19()
    assert s19.chunk_length_seconds(0, 0.4) >= 1
    assert s19.chunk_length_seconds(300, 0.0) >= 1
    assert s19.chunk_length_seconds(0, -5.0) >= 1


def test_ingest_loop_uses_chunk_length_seconds():
    src = s19_source()
    assert "chunk_length_seconds(" in src.split("def ingest_loop", 1)[1].split("\ndef ", 1)[0], (
        "ingest_loop must go through chunk_length_seconds so --chunk-seconds 0 is honoured"
    )


def test_stream_chunk_ack_timeout_covers_a_multi_hour_single_chunk():
    """単一ストリームでも gRPC Ack 待ちがチャンク長より短くなってはいけない（短いと
    「サーバは受理済みなのにクライアントが timeout」になり、A/B の対照側だけ chunk_errors が
    立って比較にならない）。ストリームは張らず wait_for の timeout だけを覗く。"""
    s19 = load_s19()
    seconds = s19.chunk_length_seconds(0, 5 * 3600.0)

    fake_grpc = types.ModuleType("grpc")

    class _Ch:
        async def __aenter__(self):
            return self

        async def __aexit__(self, *exc):
            return False

    fake_grpc.aio = types.SimpleNamespace(insecure_channel=lambda target: _Ch())

    class _Stub:
        def __init__(self, ch):
            pass

        def StreamTelemetry(self, gen):  # noqa: N802 — gRPC 生成コードの名前に合わせる
            async def _run():
                async for _ in gen:
                    pass
                return types.SimpleNamespace(accepted=0)

            return _run()

    pb2 = types.SimpleNamespace(TelemetryFrame=lambda **kw: kw)
    pb2g = types.SimpleNamespace(GatewayIngressStub=_Stub)

    captured: dict = {}

    async def fake_wait_for(awaitable, timeout=None):
        captured["timeout"] = timeout
        awaitable.close()  # 5h 分のフレームを実際に流さない
        return types.SimpleNamespace(accepted=0)

    with mock.patch.dict(sys.modules, {"grpc": fake_grpc}), \
         mock.patch.object(asyncio, "wait_for", fake_wait_for):
        asyncio.run(s19.stream_chunk(pb2, pb2g, "localhost:5051", "GW", ["p0"], 6.2167, seconds))

    assert captured["timeout"] >= seconds, (
        f"ack timeout {captured['timeout']} must not be shorter than the {seconds}s chunk"
    )


def _fake_grpc_stack():
    """stream_chunk を実際に走らせるための最小 gRPC スタブ（gen を最後まで drain する）。"""
    fake_grpc = types.ModuleType("grpc")

    class _Ch:
        async def __aenter__(self):
            return self

        async def __aexit__(self, *exc):
            return False

    fake_grpc.aio = types.SimpleNamespace(insecure_channel=lambda target: _Ch())

    class _Stub:
        def __init__(self, ch):
            pass

        def StreamTelemetry(self, gen):  # noqa: N802 — gRPC 生成コードの名前に合わせる
            async def _run():
                n = 0
                async for _ in gen:
                    n += 1
                return types.SimpleNamespace(accepted=n)

            return _run()

    pb2 = types.SimpleNamespace(TelemetryFrame=lambda **kw: kw)
    pb2g = types.SimpleNamespace(GatewayIngressStub=_Stub)
    return fake_grpc, pb2, pb2g


def test_stream_chunk_reports_progress_while_the_stream_is_still_open():
    """`--chunk-seconds 0` の対照 run はチャンク境界が 1 つも来ない。途中経過を出さないと、
    5 時間目に中断された run の ingest 記録が 0 バイトになり、既定 300s 側より復元性が劣る
    （対照が本命より脆いのは倒錯している）。"""
    s19 = load_s19()
    fake_grpc, pb2, pb2g = _fake_grpc_stack()
    seen: list[int] = []

    with mock.patch.dict(sys.modules, {"grpc": fake_grpc}):
        sent, accepted, err = asyncio.run(
            s19.stream_chunk(pb2, pb2g, "localhost:5051", "GW", ["p0"], rate=200.0, seconds=1,
                              progress=seen.append, progress_interval=0.0))

    assert err is None
    assert sent == accepted == 200
    assert len(seen) >= 2, "progress must be reported before the stream completes"
    assert seen == sorted(seen), "progress must be monotonic (it is a running `sent` count)"
    assert max(seen) <= sent


def test_stream_chunk_defaults_to_a_progress_cadence_far_below_a_multi_hour_chunk():
    s19 = load_s19()
    assert 0 < s19.PROGRESS_INTERVAL_S <= 300.0


def test_ingest_loop_records_how_many_streams_it_actually_opened():
    """`chunk_seconds: 0` は「1 本で流す**指定**」であって「1 本で流れた」ではない — 途中で 1 度でも
    失敗すれば張り直している。A/B を「1 本 vs N 本」として読んでよいのは実測値が裏付けたときだけ
    なので、実際に張った本数を結果に載せる。"""
    s19 = load_s19()

    async def fake_stream_chunk(pb2, pb2g, target, gw, points, rate, seconds,
                                 progress=None, progress_interval=None):  # noqa: ARG001
        await asyncio.sleep(0.01)
        return 10, 10, None

    args = types.SimpleNamespace(chunk_seconds=1, ingress="localhost:5051", rate=6.0)

    with tempfile.TemporaryDirectory() as out_dir:
        with mock.patch.object(s19, "stream_chunk", side_effect=fake_stream_chunk):
            result = asyncio.run(s19.ingest_loop(None, None, args, "GW", ["p0"], out_dir,
                                                  time.monotonic() + 0.05))
        lines = [json.loads(x) for x in
                 Path(out_dir, "ingest-timeseries.jsonl").read_text(encoding="utf-8").splitlines()]

    assert result["chunk_count"] >= 1
    chunk_records = [r for r in lines if r.get("kind") == "chunk"]
    assert result["chunk_count"] == len(chunk_records), (
        "chunk_count must equal the number of streams actually journalled"
    )
    assert result["sent"] == 10 * result["chunk_count"]


def test_run_puts_chunk_count_next_to_chunk_errors_in_the_result_metrics():
    src = s19_source()
    metrics_block = src.split("metrics = {", 1)[1].split("}", 1)[0]
    assert '"chunk_count": ingest_result["chunk_count"]' in metrics_block, (
        "the realized stream-open count must be in E10-soak.json, not only in the raw jsonl"
    )


# ── 配線: CLI / 子プロセス / 結果ファイル / .sh / KPI ─────────────────────────


def test_cli_exposes_prometheus_and_runtime_job_flags():
    src = s19_source()
    assert '"--prometheus"' in src
    assert '"--runtime-job"' in src
    assert 'os.environ.get("PROMETHEUS_URL", "")' in src, (
        "--prometheus must default to $PROMETHEUS_URL and be empty (= disabled) when unset"
    )
    assert "building-os-connector-worker" in src, (
        "--runtime-job default must be the OTEL_SERVICE_NAME the compose file sets"
    )


def test_resource_child_process_receives_the_runtime_sampling_flags():
    """サンプリングは別プロセス（grpc.aio と fork の同居回避）。親だけ知っていても意味がない。"""
    src = s19_source()
    spawn = src.split("resource_proc = subprocess.Popen(", 1)[1].split("])", 1)[0]
    assert "--prometheus" in spawn
    assert "--runtime-job" in spawn


def test_result_config_records_the_resolved_metric_names_and_the_liveness_probe():
    """全部 null の列だったのか / 別名で取っていたのか / 名前は合っていたがその job のデータが
    1 件も無かったのかを、結果ファイル単体で判別できること。"""
    src = s19_source()
    assert "runtime_metric_names" in src
    assert "runtime_metric_probe" in src


def test_shell_forwards_chunk_seconds_and_prometheus():
    sh = s19_shell_source()
    assert "CHUNK_SECONDS" in sh and "--chunk-seconds" in sh
    assert "PROMETHEUS_URL" in sh and "--prometheus" in sh


def test_shell_starts_observability_profile_only_when_prometheus_requested():
    """Prometheus/otel-collector は `profiles: [observability]` で既定スタックに居ない。
    PROMETHEUS_URL 指定時だけ上げる（= 対照側の run は被測定系を触らない）。"""
    sh = s19_shell_source()
    assert "--profile observability" in sh
    assert "building-os.prometheus" in sh
    assert "building-os.otel-collector" in sh


def test_shell_names_every_service_the_observability_profile_actually_starts():
    """compose の `building-os.otel-collector` は tempo / loki に depends_on しているので、
    collector を名指しすると **4 つ**上がる。2 つしか書かないスクリプトは blast radius を
    過小に見せ、「摂動は exporter の宛先が生きるだけ」という注記を実態より軽く読ませる。"""
    sh = s19_shell_source()
    compose = (REPO_ROOT / "docker-compose.oss.yaml").read_text(encoding="utf-8")
    collector = compose.split("building-os.otel-collector:", 1)[1].split("\n  building-os.", 1)[0]
    deps = [name for name in ("building-os.tempo", "building-os.loki", "building-os.prometheus")
            if name in collector.split("depends_on:", 1)[1]]
    assert "building-os.tempo" in deps and "building-os.loki" in deps, (
        "guard assumption: the collector still depends_on tempo/loki"
    )
    for name in deps:
        assert name in sh, f"{name} starts transitively — the script must say so"


def test_shell_waits_for_prometheus_readiness_before_starting_the_soak():
    """Prometheus は永続 TSDB の WAL リプレイ中 /api/v1/* に 503 を返す。共通の `sleep 15` で
    足りないまま本体を起動すると、系列名の解決が 503 を掴んで数時間ぶんの runtime 列が空になる。"""
    sh = s19_shell_source()
    assert "/-/ready" in sh
    assert "PROM_READY_TIMEOUT" in sh


def test_kpi_thresholds_report_the_new_runtime_series_without_gating_them():
    import yaml

    data = yaml.safe_load((REPO_ROOT / "e2e" / "kpi-thresholds.yaml").read_text(encoding="utf-8"))
    e10 = data["axes"]["E10_endurance_soak"]
    required = [
        "connector_worker_gc_heap_size_mib_growth_per_hour",
        "connector_worker_gc_committed_mib_growth_per_hour",
        "connector_worker_rss_minus_gc_committed_mib_start",
        "connector_worker_rss_minus_gc_committed_mib_end",
        "connector_worker_rss_minus_gc_committed_growth_mib_per_hour",
        # 判別指標の読み方を誤らせない補助情報（カバレッジ / 窓 / subtrahend の鮮度 / 実測本数）。
        "connector_worker_rss_minus_gc_committed_samples",
        "connector_worker_rss_samples",
        "connector_worker_rss_growth_mib_per_hour_paired",
        "connector_worker_gc_collections_growth_per_hour",
        "chunk_count",
    ]
    for key in required:
        assert key in e10, f"{key} must be reported by the E10 gate"
        # #370 は安全域を決めるための調査そのもの。閾値を置いたら結論を先取りしてしまう。
        assert e10[key]["op"] == "report", f"{key} must be informational, not a gate"


def test_kpi_thresholds_name_the_thread_series_for_the_population_it_measures():
    """取れるのは thread pool のスレッド数だけ。`thread_count` と書くと、専用スレッド（native
    リソースが張るもの）が横ばいだと読み違えられる。"""
    import yaml

    data = yaml.safe_load((REPO_ROOT / "e2e" / "kpi-thresholds.yaml").read_text(encoding="utf-8"))
    e10 = data["axes"]["E10_endurance_soak"]
    assert "connector_worker_thread_pool_thread_count_growth_per_hour" in e10
    assert "connector_worker_thread_count_growth_per_hour" not in e10


# ── seed 可視化待ち（E10 スケール） ────────────────────────────────────────────────────────────
# `s10.wait_visible` は 45s 予算 + 1 試行 60s の裸の gRPC deadline で、E5 の数点規模向け。
# E10 の既定 1,865 点では cold な PointMetadataCache のロード（点数に対して二次: 実測 1,865 点で
# 23.3s、3,000 点で 56.6s）に対して余裕がなく、超えると DEADLINE_EXCEEDED が例外として伝播して
# 数時間の soak が seed 直後に落ちる。


def test_wait_visible_at_scale_returns_true_as_soon_as_the_point_resolves():
    s19 = load_s19()
    calls = []

    def fake_stream_frames(pb2, pb2g, target, frames):
        calls.append(frames)
        return 0 if len(calls) < 3 else 1

    with mock.patch.object(s19.s10, "stream_frames", fake_stream_frames), \
         mock.patch.object(s19.time, "sleep"):
        assert s19.wait_visible_at_scale(None, None, "t", "GW", "P1", timeout_s=600) is True
    assert len(calls) == 3


def test_wait_visible_at_scale_treats_a_probe_rpc_error_as_not_yet_visible():
    """s10 の probe は `StreamTelemetry(..., timeout=60)` を裸で呼ぶので、cold load が 60s を
    超えると例外が上がる。それは「見えない」であって「run を落とす理由」ではない。"""
    s19 = load_s19()
    attempts = []

    def fake_stream_frames(pb2, pb2g, target, frames):
        attempts.append(1)
        if len(attempts) < 3:
            raise RuntimeError("DEADLINE_EXCEEDED")
        return 1

    with mock.patch.object(s19.s10, "stream_frames", fake_stream_frames), \
         mock.patch.object(s19.time, "sleep"):
        assert s19.wait_visible_at_scale(None, None, "t", "GW", "P1", timeout_s=600) is True
    assert len(attempts) == 3


def test_wait_visible_at_scale_gives_up_only_after_the_budget_is_spent():
    s19 = load_s19()
    clock = {"t": 0.0}

    def now():
        return clock["t"]

    def advance(_):
        clock["t"] += 5.0

    with mock.patch.object(s19.s10, "stream_frames", return_value=0), \
         mock.patch.object(s19.time, "sleep", advance):
        assert s19.wait_visible_at_scale(
            None, None, "t", "GW", "P1", timeout_s=60, poll_interval_s=5.0, now=now) is False
    # 予算 60s を 5s 刻みで使い切るまで諦めない（45s の s10 既定では 1,865 点に届かない）。
    assert clock["t"] >= 60.0


def test_seed_visible_timeout_is_a_cli_option_with_a_scale_appropriate_default():
    s19 = load_s19()
    src = s19_source()
    assert "--seed-visible-timeout" in src
    # 1,865 点で 23.3s、3,000 点で 56.6s（実測）。秒単位の既定では足りない。
    assert s19.DEFAULT_SEED_VISIBLE_TIMEOUT_S >= 300


def test_run_uses_the_scale_aware_probe_not_the_small_axis_one():
    s19 = load_s19()
    body = s19_source().split("async def run(args)")[1]
    assert "wait_visible_at_scale(" in body
    assert "s10.wait_visible(" not in body
