import type { Granularity } from "./types";

/**
 * Period + granularity selection for the telemetry chart (#197). Pure logic so the point-detail view
 * can drop its hard-coded "last 24h" query and let the operator pick a range and aggregation without
 * a screen change. The backend `/telemetries/query` already takes `start`/`end` + `granularity` and
 * auto-selects the tier, so this only maps a friendly preset/option onto that contract.
 */

export type PeriodPreset = "1h" | "24h" | "7d" | "30d";

/** The period control value: a preset span, or `"custom"` for an explicit start/end pair. */
export type PeriodValue = PeriodPreset | "custom";

/** `"auto"` derives the granularity from the span; the rest map straight to {@link Granularity}. */
export type GranularityOption = "auto" | Granularity;

const PRESET_MS: Record<PeriodPreset, number> = {
  "1h": 60 * 60 * 1000,
  "24h": 24 * 60 * 60 * 1000,
  "7d": 7 * 24 * 60 * 60 * 1000,
  "30d": 30 * 24 * 60 * 60 * 1000,
};

export const PERIOD_PRESETS: { value: PeriodPreset; label: string }[] = [
  { value: "1h", label: "1時間" },
  { value: "24h", label: "24時間" },
  { value: "7d", label: "7日" },
  { value: "30d", label: "30日" },
];

/** Period control options: the presets plus an explicit custom range. */
export const PERIOD_OPTIONS: { value: PeriodValue; label: string }[] = [
  ...PERIOD_PRESETS,
  { value: "custom", label: "カスタム" },
];

export const GRANULARITY_OPTIONS: {
  value: GranularityOption;
  label: string;
}[] = [
  { value: "auto", label: "自動" },
  { value: "raw", label: "生データ" },
  { value: "hour", label: "1時間" },
  { value: "day", label: "1日" },
];

export const DEFAULT_PERIOD: PeriodPreset = "24h";
export const DEFAULT_GRANULARITY: GranularityOption = "auto";

/** `[start, end]` for a preset, ending at `now`. */
export function effectiveRange(
  preset: PeriodPreset,
  now: Date,
): { start: Date; end: Date } {
  return { start: new Date(now.getTime() - PRESET_MS[preset]), end: now };
}

/**
 * A sensible default granularity for a preset span, keeping the point count bounded: sub-day spans
 * stay `raw`, a week rolls up to hourly, a month to daily.
 */
export function autoGranularity(preset: PeriodPreset): Granularity {
  switch (preset) {
    case "1h":
    case "24h":
      return "raw";
    case "7d":
      return "hour";
    case "30d":
      return "day";
  }
}

/** Resolve the query granularity from the user's choice; `"auto"` derives it from the preset. */
export function resolveGranularity(
  option: GranularityOption,
  preset: PeriodPreset,
): Granularity {
  return option === "auto" ? autoGranularity(preset) : option;
}

/** Whether a preset span crosses day boundaries — the axis then needs dates, not just `HH:mm`. */
export function spansMultipleDays(preset: PeriodPreset): boolean {
  return preset === "7d" || preset === "30d";
}

const DAY_MS = 24 * 60 * 60 * 1000;

/** Span-based auto granularity for a custom range (mirrors {@link autoGranularity}'s preset cutoffs). */
export function autoGranularityForSpan(start: Date, end: Date): Granularity {
  const ms = end.getTime() - start.getTime();
  if (ms <= DAY_MS) return "raw";
  if (ms <= 7 * DAY_MS) return "hour";
  return "day";
}

/** Whether a custom range crosses a day boundary — the axis then needs dates, not just `HH:mm`. */
export function rangeSpansMultipleDays(start: Date, end: Date): boolean {
  return end.getTime() - start.getTime() > DAY_MS;
}

/**
 * Validate a `[start, end]` pair from `datetime-local` inputs. Returns a JA message when the pair is
 * present but invalid (start ≥ end, or a future date), otherwise `null`. An incomplete pair (either
 * side empty) is NOT an error — it just leaves the action disabled — so it returns `null` too, to
 * avoid nagging the operator before they finish typing.
 */
export function dateRangeError(
  start: string,
  end: string,
  now: Date,
): string | null {
  if (!start || !end) return null;
  const s = new Date(start).getTime();
  const e = new Date(end).getTime();
  if (Number.isNaN(s) || Number.isNaN(e))
    return "日時の形式が正しくありません。";
  if (s >= e) return "開始日時は終了日時より前にしてください。";
  if (e > now.getTime()) return "終了日時に未来の日時は指定できません。";
  if (s > now.getTime()) return "開始日時に未来の日時は指定できません。";
  return null;
}

/** Whether a `[start, end]` pair is complete and valid (safe to query/download). */
export function isValidDateRange(
  start: string,
  end: string,
  now: Date,
): boolean {
  return (
    Boolean(start) && Boolean(end) && dateRangeError(start, end, now) === null
  );
}
