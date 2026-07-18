"use client";

import { fetchGateways as defaultFetchGateways } from "@/lib/admin/gateways";
import { buildAttentionList, type NamedPoint } from "@/lib/home/aggregate";
import type { HomeLoaders } from "@/lib/home/loaders";
import type { ResourceRef } from "@/lib/resources/types";
import {
  type FreshnessSummary,
  type PointFreshness,
  summarizeFreshness,
} from "@/lib/telemetry/freshness";
import { formatAge } from "@/lib/telemetry/freshness-format";
import Link from "next/link";
import { useEffect, useState } from "react";
import { GatewayStatusPanel, type GatewaysFetcher } from "./gateway-status-panel";

/**
 * Operator home (#158): a non-disruptive landing that answers "何が届いていないか" at a glance —
 * pick a building + floor, see fresh/stale/missing counts and the worst-first list of points that
 * need attention. Admins additionally see a light gateway overview. All data access is injected via
 * {@link HomeLoaders} so the view is unit-testable offline; the route wires the production loaders.
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
      .catch((e) => active && setError(errMsg(e, "フロアの取得に失敗しました")));
    return () => {
      active = false;
    };
  }, [loaders, buildingDtId]);

  // Load the floor's points + freshness when the floor changes.
  useEffect(() => {
    if (!floorDtId) {
      setNamed([]);
      setFreshness([]);
      return;
    }
    let active = true;
    setLoadingFloor(true);
    setError(null);
    // Reset the previous floor's data so the summary cards don't show its counts mid-switch.
    setNamed([]);
    setFreshness([]);
    (async () => {
      const points = await loaders.loadFloorPoints(floorDtId);
      if (!active) return;
      setNamed(points);
      const fresh = await loaders.loadFreshness(points);
      if (!active) return;
      setFreshness(fresh);
    })()
      .catch((e) => active && setError(errMsg(e, "テレメトリの取得に失敗しました")))
      .finally(() => active && setLoadingFloor(false));
    return () => {
      active = false;
    };
  }, [loaders, floorDtId]);

  const summary: FreshnessSummary = summarizeFreshness(freshness);
  const attention = buildAttentionList(named, freshness);

  return (
    <div data-testid="operator-home" className="space-y-6 p-6">
      <header>
        <h1 className="text-xl font-semibold text-gray-800">ホーム</h1>
        <p className="mt-1 text-sm text-gray-600">
          フロアを選ぶと、テレメトリの鮮度と対応が必要なポイントを確認できます。
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

      <section className="grid grid-cols-3 gap-4" data-testid="home-summary">
        <SummaryCard label="最新" value={summary.fresh} testid="summary-fresh" tone="text-green-800" />
        <SummaryCard label="鮮度切れ" value={summary.stale} testid="summary-stale" tone="text-amber-800" />
        <SummaryCard label="欠測" value={summary.missing} testid="summary-missing" tone="text-gray-700" />
      </section>

      <section>
        <h2 className="mb-2 text-sm font-semibold text-gray-700">要対応ポイント</h2>
        {loadingFloor ? (
          <p className="text-sm text-gray-600">読み込み中…</p>
        ) : attention.length === 0 ? (
          <p data-testid="home-attention-empty" className="text-sm text-gray-600">
            対応が必要なポイントはありません。
          </p>
        ) : (
          <ul data-testid="home-attention-list" className="divide-y divide-gray-100 rounded-lg border border-gray-200">
            {attention.map((item) => (
              <li key={item.pointId} data-testid="home-attention-row">
                <Link
                  href={`/points/${encodeURIComponent(item.pointId)}`}
                  data-testid="home-attention-link"
                  className="flex items-center justify-between gap-3 px-4 py-2 text-sm hover:bg-gray-50"
                >
                  <span className="min-w-0">
                    <span className="block truncate font-medium text-gray-800">{item.name}</span>
                    {(item.spaceName || item.deviceName) && (
                      <span className="block truncate text-xs text-gray-600">
                        {[item.spaceName, item.deviceName].filter(Boolean).join(" / ")}
                      </span>
                    )}
                  </span>
                  <span
                    data-testid={`attention-${item.status}`}
                    className={`shrink-0 ${item.status === "missing" ? "text-gray-700" : "text-amber-800"}`}
                  >
                    {item.status === "missing"
                      ? "欠測（データなし）"
                      : `鮮度切れ（${item.ageSeconds !== null ? formatAge(item.ageSeconds) : "不明"}）`}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>

      {isAdmin && <GatewayStatusPanel fetchGateways={fetchGateways} />}
    </div>
  );
}

function SummaryCard({
  label,
  value,
  testid,
  tone,
}: {
  label: string;
  value: number;
  testid: string;
  tone: string;
}) {
  return (
    <div data-testid={testid} className="rounded-lg border border-gray-200 p-4 text-center">
      <div className={`text-2xl font-bold ${tone}`}>{value}</div>
      <div className="mt-1 text-xs text-gray-600">{label}</div>
    </div>
  );
}

function errMsg(e: unknown, fallback: string): string {
  return e instanceof Error ? e.message : fallback;
}
