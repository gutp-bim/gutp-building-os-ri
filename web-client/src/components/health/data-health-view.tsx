"use client";

/**
 * データ品質（`/health`, #453）の一覧ビュー。
 *
 * 「何が届いていないか」を Point 単位で一覧し、そこから Point 詳細へ送り出す運用画面。
 * 判定そのものはサーバ側の分類器が正本で、ここは**描画と絞り込みだけ**を担う（ADR-0007）。
 *
 * 設計上の約束:
 * - **データアクセスは props で注入する**（{@link HealthLoaders}）。オフラインで単体テストできる
 *   ようにするためで、本番の結線は `page-component.tsx` が行う。
 * - **検索条件は URL が正本**。ビューは条件を持たず、変更は `onQueryChange` で親へ上げる。
 * - **鮮度と値異常は別の列**。1 つの Point が「最新かつ値異常」であることを潰さない。
 * - **`dataComplete=false` のときは件数を確定値として見せない**。インデックス同期中に
 *   「欠測 47 件」と言い切ると、実際には未走査なだけの Point を障害として扱わせてしまう。
 */

import { HelpButton } from "@/components/help/help-button";
import { Pagination } from "@/components/table/Pagination";
import { InlineBanner } from "@/components/ui/inline-banner";
import type { HealthLoaders } from "@/lib/health/loaders";
import {
  explainRowThreshold,
  formatAge,
  missingReasonLabel,
  type HealthRow,
} from "@/lib/health/mapping";
import {
  DEFAULT_HEALTH_QUERY,
  serializeHealthQuery,
  type HealthQuery,
} from "@/lib/health/query";
import type { HealthPage, HealthSummary } from "@/lib/health/repository";
import type { ResourceRef } from "@/lib/resources/types";
import { formatDurationJa } from "@/lib/telemetry/threshold-explain";
import { resolveUnitLabel } from "@/lib/utils/helper/telemetry-helper";
import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";
import { HealthFilterBar } from "./health-filter-bar";
import {
  GatewayConnectionBadge,
  HealthAlarmBadge,
  HealthFreshnessBadge,
} from "./health-status-badge";

/** 表示上の「値なし」。 */
const DASH = "—";

const COLUMNS = [
  "Point",
  "鮮度",
  "値",
  "値異常",
  "Last Seen",
  "期待周期",
  "判定閾値",
  "機器",
  "Gateway",
];

export function DataHealthView({
  loaders,
  query = DEFAULT_HEALTH_QUERY,
  onQueryChange,
}: {
  loaders: HealthLoaders;
  query?: HealthQuery;
  onQueryChange?: (next: HealthQuery) => void;
}) {
  const [page, setPage] = useState<HealthPage | null>(null);
  const [summary, setSummary] = useState<HealthSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [buildings, setBuildings] = useState<ResourceRef[]>([]);
  const [floors, setFloors] = useState<ResourceRef[]>([]);
  const [gatewayIds, setGatewayIds] = useState<string[]>([]);

  // 条件は URL 文字列に畳んで比較する。親が毎レンダで同じ内容の別オブジェクトを渡しても
  // 取得が走り続けないようにするため（URL が正本なので同値判定も URL でよい）。
  const queryKey = useMemo(
    () => serializeHealthQuery(query).toString(),
    [query],
  );
  const queryRef = useRef(query);
  queryRef.current = query;

  useEffect(() => {
    let active = true;
    const current = queryRef.current;
    setLoading(true);
    setError(null);

    loaders
      .loadHealth(current)
      .then((p) => {
        if (!active) return;
        setPage(p);
      })
      .catch((e) => {
        if (!active) return;
        setPage(null);
        setError(errMsg(e, "データ品質の取得に失敗しました"));
      })
      .finally(() => {
        if (active) setLoading(false);
      });

    // 集計は一覧の付随情報。失敗しても一覧は出す（チップの件数を落とすだけ）。
    loaders
      .loadSummary(current)
      .then((s) => active && setSummary(s))
      .catch(() => active && setSummary(null));

    return () => {
      active = false;
    };
  }, [loaders, queryKey]);

  useEffect(() => {
    let active = true;
    loaders
      .loadBuildings()
      .then((bs) => active && setBuildings(bs))
      .catch(() => active && setBuildings([]));
    loaders
      .loadGatewayIds()
      .then((ids) => active && setGatewayIds(ids))
      .catch(() => active && setGatewayIds([]));
    return () => {
      active = false;
    };
  }, [loaders]);

  const buildingDtId = query.buildingDtId;
  useEffect(() => {
    if (buildingDtId === undefined) {
      setFloors([]);
      return;
    }
    let active = true;
    loaders
      .loadFloors(buildingDtId)
      .then((fs) => active && setFloors(fs))
      .catch(() => active && setFloors([]));
    return () => {
      active = false;
    };
  }, [loaders, buildingDtId]);

  const rows = useMemo(() => page?.rows ?? [], [page]);
  const total = page?.total ?? 0;
  const limit = page?.limit ?? query.limit;
  const offset = page?.offset ?? query.offset;
  // 一覧と集計のどちらかが「揃っていない」と言えば暫定。厳しい側に倒す。
  const dataComplete =
    (page?.dataComplete ?? true) && (summary?.dataComplete ?? true);

  /** 絞り込みの変更。条件が変われば頁位置は意味を失うので先頭に戻す。 */
  const applyFilter = (patch: Partial<HealthQuery>) =>
    onQueryChange?.({ ...query, ...patch, offset: 0 });

  /** 頁送り。こちらは offset をそのまま扱う。 */
  const applyPaging = (patch: Partial<HealthQuery>) =>
    onQueryChange?.({ ...query, ...patch });

  // 選択肢に無い gateway（管理者 API を読めない operator）でも、表示中の行から拾って絞り込める。
  const gatewayOptions = useMemo(() => {
    const ids = new Set(gatewayIds);
    for (const row of rows) if (row.gatewayId) ids.add(row.gatewayId);
    if (query.gatewayId) ids.add(query.gatewayId);
    return [...ids].sort((a, b) => a.localeCompare(b));
  }, [gatewayIds, rows, query.gatewayId]);

  return (
    <div data-testid="data-health-view" className="space-y-4 p-6">
      <header>
        <div className="flex items-center gap-2">
          <h1 className="text-xl font-semibold text-gray-800">データ品質</h1>
          <HelpButton helpKey="operator.health" />
        </div>
        <p className="mt-1 text-sm text-gray-600">
          Point
          ごとに、データが届いているか（鮮度）と、届いた値が閾値の内側か（値異常）を確認できます。
        </p>
      </header>

      {!dataComplete && (
        <InlineBanner tone="warn" testId="health-incomplete-banner">
          同期中 —
          最終受信インデックスを走査中のため、判定と件数は暫定です。確定するまで欠測件数は表示しません。
        </InlineBanner>
      )}

      <HealthFilterBar
        query={query}
        summary={summary}
        dataComplete={dataComplete}
        buildings={buildings}
        floors={floors}
        gatewayIds={gatewayOptions}
        onChange={applyFilter}
      />

      {error && (
        <InlineBanner tone="error" testId="health-error">
          {error}
        </InlineBanner>
      )}

      {loading ? (
        <p data-testid="health-loading" className="text-sm text-gray-600">
          読み込み中…
        </p>
      ) : error ? null : (
        <>
          <p className="text-sm text-gray-600">
            {dataComplete
              ? `${total.toLocaleString("ja-JP")} 件`
              : `${total.toLocaleString("ja-JP")} 件（暫定）`}
          </p>

          {/* 列が多いので、狭い画面では表だけを横スクロールさせる（本文は折り返さない）。 */}
          <div className="overflow-x-auto rounded-lg border border-gray-200">
            <table data-testid="health-table" className="min-w-full text-sm">
              <thead className="bg-gray-50 text-left text-xs text-gray-600">
                <tr>
                  {COLUMNS.map((c) => (
                    <th
                      key={c}
                      scope="col"
                      className="whitespace-nowrap px-3 py-2"
                    >
                      {c}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {rows.length === 0 ? (
                  <tr>
                    <td
                      data-testid="health-empty"
                      colSpan={COLUMNS.length}
                      className="px-3 py-6 text-center text-gray-600"
                    >
                      条件に一致する Point はありません。
                    </td>
                  </tr>
                ) : (
                  rows.map((row) => (
                    <HealthTableRow key={row.pointId} row={row} />
                  ))
                )}
              </tbody>
            </table>
          </div>

          {total > 0 && (
            <Pagination
              currentPage={Math.floor(offset / Math.max(1, limit)) + 1}
              totalPages={Math.max(1, Math.ceil(total / Math.max(1, limit)))}
              pageSize={limit}
              onPageChange={(p) =>
                applyPaging({ offset: Math.max(0, (p - 1) * limit) })
              }
              onPageSizeChange={(size) => applyFilter({ limit: size })}
            />
          )}
        </>
      )}
    </div>
  );
}

function HealthTableRow({ row }: { row: HealthRow }) {
  return (
    <tr data-testid="health-row" className="hover:bg-gray-50">
      <td className="px-3 py-2">
        {/* 行の入口はリンク 1 本にする（`tr` の onClick はキーボードから辿れない）。 */}
        <Link
          href={`/points/${encodeURIComponent(row.pointId)}`}
          data-testid="health-row-link"
          className="block rounded focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
        >
          <span className="block font-medium text-gray-800">{row.name}</span>
          <span className="block text-xs text-gray-500">{row.pointId}</span>
        </Link>
      </td>
      <td className="whitespace-nowrap px-3 py-2">
        <HealthFreshnessBadge status={row.freshnessStatus} />
        {row.missingReason !== null && (
          <span className="mt-0.5 block text-xs text-gray-600">
            {missingReasonLabel(row.missingReason)}
          </span>
        )}
      </td>
      <td className="whitespace-nowrap px-3 py-2 text-gray-800">
        {valueText(row)}
      </td>
      <td className="whitespace-nowrap px-3 py-2">
        <HealthAlarmBadge status={row.alarmStatus} violated={row.violated} />
      </td>
      <td
        className="whitespace-nowrap px-3 py-2 text-gray-700"
        title={row.lastSeen ?? undefined}
      >
        {formatAge(row.ageSeconds)}
      </td>
      <td className="whitespace-nowrap px-3 py-2 text-gray-700">
        {row.expectedIntervalSeconds === null
          ? DASH
          : formatDurationJa(row.expectedIntervalSeconds)}
      </td>
      {/* 閾値の根拠（期待周期 × 倍率）は title で添える。列に出すと表が読めなくなる。 */}
      <td
        className="whitespace-nowrap px-3 py-2 text-gray-700"
        title={explainRowThreshold(row)}
      >
        {formatDurationJa(row.thresholdSeconds)}
      </td>
      <td className="whitespace-nowrap px-3 py-2 text-gray-700">
        {row.deviceName ?? DASH}
      </td>
      <td className="whitespace-nowrap px-3 py-2 text-gray-700">
        {row.gatewayId === null ? (
          DASH
        ) : (
          <span className="inline-flex items-center gap-2">
            {row.gatewayId}
            <GatewayConnectionBadge connected={row.gatewayConnected} />
          </span>
        )}
      </td>
    </tr>
  );
}

/**
 * 値の表示。単位があれば添える。値が無い（欠測など）行はダッシュ。
 *
 * 単位は twin に QUDT IRI でも短縮コード（`degC`）でも入るので、Point 詳細と同じ
 * `resolveUnitLabel` で解決する。生値のまま出すと一覧だけ "degC"、詳細は "°C" になる。
 */
function valueText(row: HealthRow): string {
  if (row.value === null) return DASH;
  const unit = resolveUnitLabel(row.unit);
  return unit ? `${row.value} ${unit}` : `${row.value}`;
}

function errMsg(e: unknown, fallback: string): string {
  return e instanceof Error ? e.message : fallback;
}
