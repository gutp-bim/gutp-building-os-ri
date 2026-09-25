"use client";

import { fetchGateways as defaultFetchGateways } from "@/lib/admin/gateways";
import type { HealthSummary } from "@/lib/health/repository";
import {
  activeAlarms,
  buildAttentionList,
  type AttentionItem,
  type NamedPoint,
} from "@/lib/home/aggregate";
import type { HomeLoaders } from "@/lib/home/loaders";
import type { OperationsSummary } from "@/lib/operations/repository";
import type { ResourceRef } from "@/lib/resources/types";
import { formatKpi } from "@/lib/system-status/format";
import { summarizeAlarms, type PointAlarm } from "@/lib/telemetry/alarm";
import type { PointFreshness } from "@/lib/telemetry/freshness";
import { formatAge } from "@/lib/telemetry/freshness-format";
import Link from "next/link";
import { useEffect, useState } from "react";
import {
  GatewayStatusPanel,
  type GatewaysFetcher,
} from "./gateway-status-panel";

/** Sentinel floor value that aggregates every floor of the selected building (#158 Phase 2). */
const ALL_FLOORS = "__all__";

/**
 * Operator home (#158): a non-disruptive landing that answers "何が届いていないか" at a glance —
 * pick a building + floor (or **すべてのフロア** for a building-wide alert view), see fresh/stale/missing
 * counts and the worst-first list of points that need attention. Admins additionally see a light
 * gateway overview. All data access is injected via {@link HomeLoaders} so the view is unit-testable
 * offline; the route wires the production loaders.
 */
export function OperatorHome({
  loaders,
  isAdmin,
  fetchGateways = defaultFetchGateways,
}: {
  loaders: HomeLoaders;
  isAdmin: boolean;
  fetchGateways?: GatewaysFetcher;
}) {
  const [buildings, setBuildings] = useState<ResourceRef[]>([]);
  const [buildingDtId, setBuildingDtId] = useState<string | null>(null);
  const [floors, setFloors] = useState<ResourceRef[]>([]);
  const [floorDtId, setFloorDtId] = useState<string | null>(null);
  const [freshness, setFreshness] = useState<PointFreshness[]>([]);
  const [alarms, setAlarms] = useState<PointAlarm[]>([]);
  const [named, setNamed] = useState<NamedPoint[]>([]);
  const [healthSummary, setHealthSummary] = useState<HealthSummary | null>(
    null,
  );
  const [loadingFloor, setLoadingFloor] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [opsSummary, setOpsSummary] = useState<OperationsSummary | null>(
    null,
  );

  // Data throughput (#451 Phase 1) is a Platform-wide KPI, independent of the building/floor
  // selection — fetch it once. A failure here degrades to "no card" rather than the page error
  // banner: it is a supplementary KPI, not something the operator came here to see.
  useEffect(() => {
    let active = true;
    loaders
      .loadOperationsSummary()
      .then((s) => active && setOpsSummary(s))
      .catch(() => {
        /* card just stays hidden */
      });
    return () => {
      active = false;
    };
  }, [loaders]);

  // Load the building list once; auto-select the first.
  useEffect(() => {
    let active = true;
    loaders
      .loadBuildings()
      .then((bs) => {
        if (!active) return;
        setBuildings(bs);
        setBuildingDtId((cur) => cur ?? bs[0]?.dtId ?? null);
      })
      .catch((e) => active && setError(errMsg(e, "建物の取得に失敗しました")));
    return () => {
      active = false;
    };
  }, [loaders]);

  // Load floors when the building changes; auto-select the first.
  useEffect(() => {
    if (!buildingDtId) {
      setFloors([]);
      setFloorDtId(null);
      return;
    }
    let active = true;
    setError(null);
    loaders
      .loadFloors(buildingDtId)
      .then((fs) => {
        if (!active) return;
        setFloors(fs);
        setFloorDtId(fs[0]?.dtId ?? null);
      })
      .catch(
        (e) => active && setError(errMsg(e, "フロアの取得に失敗しました")),
      );
    return () => {
      active = false;
    };
  }, [loaders, buildingDtId]);

  // Load the selected floor's points + freshness — or every floor's when "すべてのフロア" is chosen.
  // Also loads the registered-count / Fresh-rate KPI from the server-side summary (#452), scoped to
  // the same building/floor selection — a single request for "すべてのフロア" rather than a
  // per-floor client-side re-aggregation of `loadFreshness` (#451).
  useEffect(() => {
    const floorIds =
      floorDtId === ALL_FLOORS
        ? floors.map((f) => f.dtId)
        : floorDtId
          ? [floorDtId]
          : [];
    if (!buildingDtId || floorIds.length === 0) {
      setNamed([]);
      setFreshness([]);
      setAlarms([]);
      setHealthSummary(null);
      return;
    }
    const scopedBuildingDtId = buildingDtId;
    const summaryFloorDtId =
      floorDtId === ALL_FLOORS ? undefined : (floorDtId ?? undefined);
    let active = true;
    setLoadingFloor(true);
    setError(null);
    // Reset the previous selection's data so the summary cards don't show its counts mid-switch.
    setNamed([]);
    setFreshness([]);
    setAlarms([]);
    setHealthSummary(null);
    (async () => {
      const [perFloor, health] = await Promise.all([
        Promise.all(floorIds.map((id) => loaders.loadFloorPoints(id))),
        loaders.loadHealthSummary(scopedBuildingDtId, summaryFloorDtId),
      ]);
      if (!active) return;
      const points = perFloor.flat();
      setNamed(points);
      setHealthSummary(health);
      // Freshness (arrival) and alarms (value) are independent axes — fetch both.
      const [fresh, al] = await Promise.all([
        loaders.loadFreshness(points),
        loaders.loadAlarms(points),
      ]);
      if (!active) return;
      setFreshness(fresh);
      setAlarms(al);
    })()
      .catch(
        (e) => active && setError(errMsg(e, "テレメトリの取得に失敗しました")),
      )
      .finally(() => active && setLoadingFloor(false));
    return () => {
      active = false;
    };
  }, [loaders, buildingDtId, floorDtId, floors]);

  // Only alarm on points whose data is fresh — a breach from a stale/missing point is not a live value
  // alarm, and surfaces as its freshness issue instead (#158 Phase 2a).
  const alarms_ = activeAlarms(alarms, freshness);
  const alarmSummary = summarizeAlarms(alarms_);
  const attention = buildAttentionList(named, freshness, alarms_);

  return (
    <div data-testid="operator-home" className="space-y-6 p-6">
      <header>
        <h1 className="text-xl font-semibold text-gray-800">ホーム</h1>
        <p className="mt-1 text-sm text-gray-600">
          フロア（または「すべてのフロア」で建物全体）を選ぶと、テレメトリの鮮度と対応が必要なポイントを確認できます。
        </p>
      </header>

      <div className="flex flex-wrap gap-4">
        <label className="flex flex-col text-sm text-gray-700">
          <span className="mb-1">建物</span>
          <select
            data-testid="home-building-select"
            className="rounded border border-gray-300 px-2 py-1"
            value={buildingDtId ?? ""}
            onChange={(e) => setBuildingDtId(e.target.value || null)}
          >
            {buildings.length === 0 && <option value="">（建物なし）</option>}
            {buildings.map((b) => (
              <option key={b.dtId} value={b.dtId}>
                {b.name}
              </option>
            ))}
          </select>
        </label>

        <label className="flex flex-col text-sm text-gray-700">
          <span className="mb-1">フロア</span>
          <select
            data-testid="home-floor-select"
            className="rounded border border-gray-300 px-2 py-1"
            value={floorDtId ?? ""}
            onChange={(e) => setFloorDtId(e.target.value || null)}
          >
            {floors.length === 0 && <option value="">（フロアなし）</option>}
            {floors.length > 0 && (
              <option value={ALL_FLOORS}>すべてのフロア（建物全体）</option>
            )}
            {floors.map((f) => (
              <option key={f.dtId} value={f.dtId}>
                {f.name}
              </option>
            ))}
          </select>
        </label>
      </div>

      {error && (
        <p data-testid="home-error" className="text-sm text-red-700">
          {error}
        </p>
      )}

      <section
        className="grid grid-cols-2 gap-4 sm:grid-cols-5"
        data-testid="home-summary"
      >
        <SummaryCard
          label="登録ポイント"
          value={healthSummary?.totalPoints ?? 0}
          testid="summary-total"
          tone="text-gray-800"
          href="/health"
        />
        <SummaryCard
          // 「最新」は到着軸（データが届いているか）の状態であって値の正常性ではないので、
          // 「正常」とは呼ばない。最新かつ値異常のポイントは成立する。
          // 呼び名は freshnessLabel（/health 側）と揃える — 飛んだ先で別の語になると同じ状態に
          // 見えなくなる。
          label="最新"
          value={healthSummary?.fresh ?? 0}
          sub={freshRateLabel(healthSummary)}
          testid="summary-fresh"
          tone="text-green-800"
          href="/health?freshness=fresh"
        />
        <SummaryCard
          label="鮮度切れ"
          value={healthSummary?.stale ?? 0}
          testid="summary-stale"
          tone="text-amber-800"
          href="/health?freshness=stale"
        />
        <SummaryCard
          label="欠測"
          value={healthSummary?.missing ?? 0}
          testid="summary-missing"
          tone="text-gray-700"
          href="/health?freshness=missing"
        />
        <SummaryCard
          label="値異常"
          value={alarmSummary.critical + alarmSummary.warn}
          testid="summary-alarm"
          tone="text-red-700"
          href="/health?alarm=warn,critical"
        />
      </section>

      <DataThroughputPanel summary={opsSummary} />

      <section>
        <h2 className="mb-2 text-sm font-semibold text-gray-700">
          要対応ポイント
        </h2>
        {loadingFloor ? (
          <p className="text-sm text-gray-600">読み込み中…</p>
        ) : attention.length === 0 ? (
          <p
            data-testid="home-attention-empty"
            className="text-sm text-gray-600"
          >
            対応が必要なポイントはありません。
          </p>
        ) : (
          <ul
            data-testid="home-attention-list"
            className="divide-y divide-gray-100 rounded-lg border border-gray-200"
          >
            {attention.map((item) => (
              <li key={item.pointId} data-testid="home-attention-row">
                <Link
                  href={`/points/${encodeURIComponent(item.pointId)}`}
                  data-testid="home-attention-link"
                  className="flex items-center justify-between gap-3 px-4 py-2 text-sm hover:bg-gray-50"
                >
                  <span className="min-w-0">
                    <span className="block truncate font-medium text-gray-800">
                      {item.name}
                    </span>
                    {(item.spaceName || item.deviceName) && (
                      <span className="block truncate text-xs text-gray-600">
                        {[item.spaceName, item.deviceName]
                          .filter(Boolean)
                          .join(" / ")}
                      </span>
                    )}
                  </span>
                  <span
                    data-testid={`attention-${item.status}`}
                    className={`shrink-0 ${ATTENTION_TONE[item.status]}`}
                  >
                    {attentionLabel(item)}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
        {/* 要対応が 0 件でもデータ品質画面への導線は残す（全体を俯瞰したいときの入口）。 */}
        <p className="mt-2 text-right">
          <Link
            href="/health"
            data-testid="home-attention-all-link"
            className="rounded text-sm text-blue-700 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            すべて見る → データ品質
          </Link>
        </p>
      </section>

      {isAdmin && <GatewayStatusPanel fetchGateways={fetchGateways} />}
    </div>
  );
}

const ATTENTION_TONE: Record<AttentionItem["status"], string> = {
  critical: "text-red-700",
  warn: "text-amber-800",
  missing: "text-gray-700",
  stale: "text-amber-800",
};

/** Right-hand status label for an attention row — value+bound for alarms, age/none for freshness. */
function attentionLabel(item: AttentionItem): string {
  switch (item.status) {
    case "critical":
    case "warn": {
      const bound = item.breach === "low" ? "下限" : "上限";
      const val = item.value ?? "—";
      const kind = item.status === "critical" ? "異常値" : "注意値";
      return `${kind}（${bound} ${val}）`;
    }
    case "missing":
      return "欠測（データなし）";
    case "stale":
      return `鮮度切れ（${item.ageSeconds != null ? formatAge(item.ageSeconds) : "不明"}）`;
  }
}

/** Fresh 率（小数 1 桁）。母数 0 のとき（未読み込み含む）は率を出さず「—」を表示する。 */
function freshRateLabel(summary: HealthSummary | null): string {
  if (!summary || summary.totalPoints <= 0) return "—";
  return `${((summary.fresh / summary.totalPoints) * 100).toFixed(1)}%`;
}

/**
 * サマリカード。カード全体が `/health` の絞り込みリンクになっていて、
 * クエリ名は `/health` 画面と共通の軸名（`freshness=` / `alarm=`）を使う。
 */
function SummaryCard({
  label,
  value,
  sub,
  testid,
  tone,
  href,
}: {
  label: string;
  value: number;
  sub?: string;
  testid: string;
  tone: string;
  href: string;
}) {
  return (
    <Link
      href={href}
      data-testid={testid}
      className="block rounded-lg border border-gray-200 p-4 text-center transition-shadow hover:shadow-md focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
    >
      <div className={`text-2xl font-bold ${tone}`}>
        {value.toLocaleString("ja-JP")}
      </div>
      {sub && <div className={`text-xs font-medium ${tone}`}>{sub}</div>}
      <div className="mt-1 text-xs text-gray-600">{label}</div>
    </Link>
  );
}

function errMsg(e: unknown, fallback: string): string {
  return e instanceof Error ? e.message : fallback;
}

/**
 * データ流量（#451 Phase 1）。Platform 由来（Prometheus 集計）の KPI で、建物/フロアのスコープを
 * 持たない。値が 1 つも無ければカードごと出さない — エラーにしない。
 *
 * `metricsAvailable` だけでは判定しない: 既定の docker-compose スタックは observability プロファイル
 * を有効にしなくても `PROMETHEUS_URL` を設定する（Prometheus 未起動でも no-op で動くようにするため、
 * CLAUDE.md の Observability セクション参照）。`IsConfigured` は「URL が空でないか」しか見ないので、
 * この既定構成では `metricsAvailable: true` のままクエリだけが両方 null になり、`!metricsAvailable`
 * だけを見ると「—」だけの空カードが出てしまう。
 */
function DataThroughputPanel({
  summary,
}: {
  summary: OperationsSummary | null;
}) {
  if (!summary || (summary.msgRate1m == null && summary.msgRate1hAvg == null))
    return null;
  const delta = throughputDeltaLabel(summary);
  return (
    <section
      data-testid="home-throughput"
      className="flex flex-wrap gap-6 rounded-lg border border-gray-200 p-4"
    >
      <div>
        <div className="text-xs text-gray-600">現在のデータ流量</div>
        <div
          data-testid="throughput-current"
          className="text-lg font-semibold text-gray-800"
        >
          {formatKpi(summary.msgRate1m, { suffix: " msg/s" })}
        </div>
      </div>
      <div>
        <div className="text-xs text-gray-600">過去1時間平均</div>
        <div
          data-testid="throughput-1h-avg"
          className="text-lg font-semibold text-gray-800"
        >
          {formatKpi(summary.msgRate1hAvg, { suffix: " msg/s" })}
          {delta && (
            <span
              data-testid="throughput-delta"
              className="ml-2 text-sm font-normal text-gray-600"
            >
              {delta}
            </span>
          )}
        </div>
      </div>
    </section>
  );
}

/** 現在値の、過去1時間平均に対する変化率。片方が無い／平均が 0 のときは出さない。 */
function throughputDeltaLabel(summary: OperationsSummary): string | null {
  const { msgRate1m, msgRate1hAvg } = summary;
  if (msgRate1m == null || !msgRate1hAvg) return null;
  const pct = ((msgRate1m - msgRate1hAvg) / msgRate1hAvg) * 100;
  const arrow = pct >= 0 ? "↑" : "↓";
  return `${arrow} ${Math.abs(pct).toFixed(1)}%`;
}
