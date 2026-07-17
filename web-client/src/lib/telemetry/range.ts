import type { Granularity } from "./types";

/**
 * Period + granularity selection for the telemetry chart (#197). Pure logic so the point-detail view
 * can drop its hard-coded "last 24h" query and let the operator pick a range and aggregation without
 * a screen change. The backend `/telemetries/query` already takes `start`/`end` + `granularity` and
 * auto-selects the tier, so this only maps a friendly preset/option onto that contract.
 */

export type PeriodPreset = "1h" | "24h" | "7d" | "30d";

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

export const GRANULARITY_OPTIONS: { value: GranularityOption; label: string }[] = [
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
