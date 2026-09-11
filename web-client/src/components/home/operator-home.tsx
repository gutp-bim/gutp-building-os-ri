"use client";

import { fetchGateways as defaultFetchGateways } from "@/lib/admin/gateways";
import {
  activeAlarms,
  buildAttentionList,
  type AttentionItem,
  type NamedPoint,
} from "@/lib/home/aggregate";
import type { HomeLoaders } from "@/lib/home/loaders";
import type { ResourceRef } from "@/lib/resources/types";
import { summarizeAlarms, type PointAlarm } from "@/lib/telemetry/alarm";
import {
  summarizeFreshness,
  type FreshnessSummary,
  type PointFreshness,
} from "@/lib/telemetry/freshness";
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
  const [loadingFloor, setLoadingFloor] = useState(false);
  const [error, setError] = useState<string | null>(null);

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
  useEffect(() => {
    const floorIds =
      floorDtId === ALL_FLOORS
        ? floors.map((f) => f.dtId)
        : floorDtId
          ? [floorDtId]
          : [];
    if (floorIds.length === 0) {
      setNamed([]);
      setFreshness([]);
      setAlarms([]);
      return;
    }
    let active = true;
    setLoadingFloor(true);
    setError(null);
    // Reset the previous selection's data so the summary cards don't show its counts mid-switch.
    setNamed([]);
    setFreshness([]);
    setAlarms([]);
    (async () => {
      const perFloor = await Promise.all(
        floorIds.map((id) => loaders.loadFloorPoints(id)),
      );
      if (!active) return;
      const points = perFloor.flat();
      setNamed(points);
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
  }, [loaders, floorDtId, floors]);

  const summary: FreshnessSummary = summarizeFreshness(freshness);
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
          value={summary.total}
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
          value={summary.fresh}
          sub={freshRateLabel(summary)}
          testid="summary-fresh"
          tone="text-green-800"
          href="/health?freshness=fresh"
        />
        <SummaryCard
          label="鮮度切れ"
          value={summary.stale}
          testid="summary-stale"
          tone="text-amber-800"
          href="/health?freshness=stale"
        />
        <SummaryCard
          label="欠測"
          value={summary.missing}
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

/** Fresh 率（小数 1 桁）。母数 0 のときは率を出さず「—」を表示する。 */
function freshRateLabel(summary: FreshnessSummary): string {
  if (summary.total <= 0) return "—";
  return `${((summary.fresh / summary.total) * 100).toFixed(1)}%`;
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
