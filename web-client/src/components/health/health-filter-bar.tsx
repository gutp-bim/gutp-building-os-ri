"use client";

/**
 * `/health` の絞り込み帯（#453）。
 *
 * **軸を混ぜない**のが設計の要点。鮮度（データが届いているか）と値異常（届いた値が閾値内か）は
 * 別のチップ群として区切って並べ、補足文でも「最新かつ値異常はあり得る」と明示する。
 * 状態は持たず（検索欄の入力途中だけ例外）、変更はすべて親に上げる — URL が正本だから。
 */

import type { HealthQuery } from "@/lib/health/query";
import type { HealthSummary } from "@/lib/health/repository";
import type { ResourceRef } from "@/lib/resources/types";
import { useEffect, useState } from "react";

/** 「N 分以上受信が無い」の選択肢。運用で使う粒度だけに絞る。 */
const OLDER_THAN_OPTIONS: { value: number; label: string }[] = [
  { value: 600, label: "10分以上" },
  { value: 3600, label: "1時間以上" },
  { value: 21600, label: "6時間以上" },
  { value: 86400, label: "1日以上" },
];

const ALL = "";

export type HealthFilterBarProps = {
  query: HealthQuery;
  /** 軸別の件数。未取得・取得失敗は null（件数を出さずダッシュにする）。 */
  summary: HealthSummary | null;
  /** false なら件数は暫定なので、確定値として見せない。 */
  dataComplete: boolean;
  buildings: ResourceRef[];
  floors: ResourceRef[];
  gatewayIds: string[];
  /** 絞り込みの変更。頁位置のリセットは親の責務。 */
  onChange: (patch: Partial<HealthQuery>) => void;
};

export function HealthFilterBar({
  query,
  summary,
  dataComplete,
  buildings,
  floors,
  gatewayIds,
  onChange,
}: HealthFilterBarProps) {
  const [searchDraft, setSearchDraft] = useState(query.q ?? "");

  // 外から（URL・他画面からの遷移）条件が変わったら入力欄も追随させる。
  useEffect(() => {
    setSearchDraft(query.q ?? "");
  }, [query.q]);

  const axisCleared = query.freshness.length === 0 && query.alarm.length === 0;
  const alarmActive =
    query.alarm.includes("warn") || query.alarm.includes("critical");

  /**
   * 鮮度チップの ON/OFF。**alarm 軸には触らない** — 2 つは独立した軸なので、値異常で絞ったまま
   * 鮮度を切り替えられないと、補足文で「別の軸です」と言っていることと操作が矛盾する。
   * 押すたびに ON/OFF するので、欠測と鮮度切れを両方立てて OR で見ることもできる。
   */
  const toggleFreshness = (value: HealthQuery["freshness"][number]) =>
    onChange({
      freshness: query.freshness.includes(value)
        ? query.freshness.filter((v) => v !== value)
        : [...query.freshness, value],
    });

  /** 値異常チップの ON/OFF。warn と critical をまとめて扱い、**鮮度軸には触らない**。 */
  const toggleAlarm = () =>
    onChange({ alarm: alarmActive ? [] : ["warn", "critical"] });

  /** チップの件数表記。暫定なら数を出さず、未取得ならダッシュ。 */
  const count = (value: number | undefined): string => {
    if (!dataComplete) return "（集計中）";
    if (value === undefined) return " —";
    return ` ${value.toLocaleString("ja-JP")}`;
  };

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-sm text-gray-700">状態</span>
        <Chip
          testId="health-chip-all"
          active={axisCleared}
          onClick={() => onChange({ freshness: [], alarm: [] })}
        >
          すべて
        </Chip>
        <Chip
          testId="health-chip-missing"
          active={query.freshness.includes("missing")}
          onClick={() => toggleFreshness("missing")}
        >
          {`欠測${count(summary?.missing)}`}
        </Chip>
        <Chip
          testId="health-chip-stale"
          active={query.freshness.includes("stale")}
          onClick={() => toggleFreshness("stale")}
        >
          {`鮮度切れ${count(summary?.stale)}`}
        </Chip>

        {/* 縦罫で「ここから先は別の軸」であることを見せる。 */}
        <span aria-hidden="true" className="mx-1 h-5 w-px bg-gray-300" />

        <Chip
          testId="health-chip-alarm"
          active={alarmActive}
          onClick={toggleAlarm}
        >
          {`値異常${count(
            summary === null
              ? undefined
              : summary.alarmWarn + summary.alarmCritical,
          )}`}
        </Chip>
      </div>

      <p className="text-xs text-gray-600">
        鮮度（データが届いているか）と値異常（届いた値が閾値の内側か）は別の軸です。
        最新のデータが届いていて、なお値が異常ということもあります。
      </p>

      <div className="flex flex-wrap items-end gap-4">
        <Field label="データ未受信">
          <select
            data-testid="health-older-than"
            className="rounded border border-gray-300 px-2 py-1 text-sm"
            value={query.olderThanSeconds ?? ALL}
            onChange={(e) =>
              onChange({
                olderThanSeconds:
                  e.target.value === ALL ? undefined : Number(e.target.value),
              })
            }
          >
            <option value={ALL}>すべて</option>
            {OLDER_THAN_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
        </Field>

        <Field label="建物">
          <select
            data-testid="health-building"
            className="rounded border border-gray-300 px-2 py-1 text-sm"
            value={query.buildingDtId ?? ALL}
            onChange={(e) =>
              // 建物が変わればフロアの選択は無意味になるので一緒に外す。
              onChange({
                buildingDtId:
                  e.target.value === ALL ? undefined : e.target.value,
                floorDtId: undefined,
              })
            }
          >
            <option value={ALL}>すべて</option>
            {buildings.map((b) => (
              <option key={b.dtId} value={b.dtId}>
                {b.name}
              </option>
            ))}
          </select>
        </Field>

        <Field label="フロア">
          <select
            data-testid="health-floor"
            className="rounded border border-gray-300 px-2 py-1 text-sm disabled:bg-gray-100"
            disabled={query.buildingDtId === undefined}
            value={query.floorDtId ?? ALL}
            onChange={(e) =>
              onChange({
                floorDtId: e.target.value === ALL ? undefined : e.target.value,
              })
            }
          >
            <option value={ALL}>すべて</option>
            {floors.map((f) => (
              <option key={f.dtId} value={f.dtId}>
                {f.name}
              </option>
            ))}
          </select>
        </Field>

        <Field label="Gateway">
          <select
            data-testid="health-gateway"
            className="rounded border border-gray-300 px-2 py-1 text-sm"
            value={query.gatewayId ?? ALL}
            onChange={(e) =>
              onChange({
                gatewayId: e.target.value === ALL ? undefined : e.target.value,
              })
            }
          >
            <option value={ALL}>すべて</option>
            {gatewayIds.map((id) => (
              <option key={id} value={id}>
                {id}
              </option>
            ))}
          </select>
        </Field>

        <form
          className="flex items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            const q = searchDraft.trim();
            onChange({ q: q === "" ? undefined : q });
          }}
        >
          <Field label="検索">
            <input
              data-testid="health-search"
              type="search"
              placeholder="Point ID・名前"
              className="rounded border border-gray-300 px-2 py-1 text-sm"
              value={searchDraft}
              onChange={(e) => setSearchDraft(e.target.value)}
            />
          </Field>
          <button
            type="submit"
            className="rounded border border-gray-300 px-3 py-1 text-sm text-gray-700 hover:bg-gray-50"
          >
            適用
          </button>
        </form>
      </div>

      {query.tags.length > 0 && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm text-gray-700">タグ</span>
          {query.tags.map((tag) => (
            <span
              key={tag}
              data-testid="health-tag"
              className="inline-flex items-center gap-1 rounded-full bg-blue-50 px-2 py-0.5 text-xs text-blue-800"
            >
              {tag}
              <button
                type="button"
                aria-label={`タグ ${tag} を外す`}
                className="text-blue-700 hover:text-blue-900"
                onClick={() =>
                  onChange({ tags: query.tags.filter((t) => t !== tag) })
                }
              >
                ×
              </button>
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function Field({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col text-sm text-gray-700">
      <span className="mb-1">{label}</span>
      {children}
    </label>
  );
}

function Chip({
  testId,
  active,
  onClick,
  children,
}: {
  testId: string;
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      data-testid={testId}
      aria-pressed={active}
      onClick={onClick}
      className={`rounded-full border px-3 py-1 text-sm ${
        active
          ? "border-blue-600 bg-blue-600 text-white"
          : "border-gray-300 bg-white text-gray-700 hover:bg-gray-50"
      }`}
    >
      {children}
    </button>
  );
}
