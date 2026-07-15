import type { PointFreshness } from "./freshness";

/**
 * Human-readable Japanese "time ago" for a whole-second age, coarsened to one unit
 * (秒 → 分 → 時間 → 日, each floored). Presentation-only; the numeric age comes from
 * {@link classifyPointFreshness}.
 */
export function formatAge(ageSeconds: number): string {
  if (ageSeconds < 60) return `${ageSeconds}秒前`;
  if (ageSeconds < 3600) return `${Math.floor(ageSeconds / 60)}分前`;
  if (ageSeconds < 86400) return `${Math.floor(ageSeconds / 3600)}時間前`;
  return `${Math.floor(ageSeconds / 86400)}日前`;
}

/** Short badge label for a point's freshness. */
export function freshnessLabel(freshness: PointFreshness): string {
  switch (freshness.status) {
    case "fresh":
      return "最新";
    case "stale":
      return freshness.ageSeconds !== null
        ? `鮮度切れ（${formatAge(freshness.ageSeconds)}）`
        : "鮮度切れ";
    case "missing":
      return "欠測";
  }
}
