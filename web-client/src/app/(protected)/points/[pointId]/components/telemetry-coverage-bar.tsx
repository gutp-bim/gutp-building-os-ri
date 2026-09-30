import {
  summarizeCoverage,
  type CoverageBucket,
  type CoverageLevel,
} from "@/lib/telemetry/coverage";
import type { PointCoverageResult } from "@/lib/telemetry/repository";
import { formatDurationJa } from "@/lib/telemetry/threshold-explain";

/**
 * 24h 受信状況バー（#457）。{@link CoverageBucket} 配列を受け取って描くだけの presentational 部品で、
 * バケットの出どころ（Phase 1 = クライアント集計、将来 = `GET /telemetries/coverage`）は知らない。
 *
 * State timeline（`telemetry-state-timeline.tsx`）は読み取り値の表なので描画は流用せず、ここでは
 * 96 本のセグメントを横に並べる。色だけに頼らないよう、要約（欠測区間数・最長欠測）をテキストと
 * aria-label の両方に出す。
 */
export type CoverageBarState =
  { kind: "loading" } | { kind: "error" } | PointCoverageResult;

const LEVEL_CLASS: Record<CoverageLevel, string> = {
  full: "bg-green-500",
  partial: "bg-amber-400",
  none: "bg-red-400",
};

const LEVEL_LABEL: Record<CoverageLevel, string> = {
  full: "受信",
  partial: "一部欠測",
  none: "欠測",
};

const UNAVAILABLE_TEXT = {
  "too-dense":
    "高頻度ポイントのため受信状況バーは表示していません（24h の全件取得を避けるため、集計 API の提供後に表示します）。",
  "no-interval": "期待更新周期が未設定のため、受信率を計算できません。",
} as const;

function timeLabel(iso: string): string {
  return new Date(iso).toLocaleTimeString("ja-JP", {
    hour: "2-digit",
    minute: "2-digit",
  });
}

function bucketTitle(b: CoverageBucket): string {
  return `${timeLabel(b.start)}–${timeLabel(b.end)} ${LEVEL_LABEL[b.level]}（受信 ${b.received} 件 / 受信率 ${Math.round(b.coverage * 100)}%）`;
}

function Bar({ buckets }: { buckets: CoverageBucket[] }) {
  const summary = summarizeCoverage(buckets);
  const gapText =
    summary.none === 0 && summary.partial === 0
      ? "欠測なし"
      : [
          summary.none > 0 ? `欠測 ${summary.none} 区間` : null,
          summary.partial > 0 ? `一部欠測 ${summary.partial} 区間` : null,
          summary.longestGapMinutes > 0
            ? `最長欠測 ${formatDurationJa(summary.longestGapMinutes * 60)}`
            : null,
        ]
          .filter(Boolean)
          .join(" / ");

  return (
    <>
      <div
        role="img"
        aria-label={`過去24時間の受信状況（15分ごと）: ${gapText}`}
        data-testid="coverage-bar"
        className="flex h-4 w-full gap-px overflow-hidden rounded"
      >
        {buckets.map((b, i) => (
          <div
            key={b.start}
            data-testid={`coverage-bucket-${i}`}
            data-level={b.level}
            title={bucketTitle(b)}
            className={`flex-1 ${LEVEL_CLASS[b.level]}`}
          />
        ))}
      </div>
      <div className="mt-1 flex justify-between text-xs text-gray-600">
        <span>24時間前</span>
        <span>現在</span>
      </div>
      <p data-testid="coverage-summary" className="mt-1 text-xs text-gray-700">
        {gapText}
      </p>
      <ul className="mt-1 flex gap-3 text-xs text-gray-600" aria-label="凡例">
        {(Object.keys(LEVEL_CLASS) as CoverageLevel[]).map((level) => (
          <li key={level} className="inline-flex items-center gap-1">
            <span
              aria-hidden="true"
              className={`inline-block h-2 w-3 rounded-sm ${LEVEL_CLASS[level]}`}
            />
            {LEVEL_LABEL[level]}
          </li>
        ))}
      </ul>
    </>
  );
}

export function TelemetryCoverageBar({ state }: { state: CoverageBarState }) {
  return (
    <section data-testid="coverage-section" className="mt-4">
      <h4 className="mb-2 text-sm font-semibold text-gray-700">受信状況 24h</h4>
      {state.kind === "loading" && (
        <p data-testid="coverage-loading" className="text-xs text-gray-600">
          読み込み中…
        </p>
      )}
      {state.kind === "error" && (
        <p data-testid="coverage-error" className="text-xs text-red-700">
          受信状況の取得に失敗しました。
        </p>
      )}
      {state.kind === "unavailable" && (
        <p data-testid="coverage-unavailable" className="text-xs text-gray-600">
          {UNAVAILABLE_TEXT[state.reason]}
        </p>
      )}
      {state.kind === "buckets" && <Bar buckets={state.buckets} />}
    </section>
  );
}
