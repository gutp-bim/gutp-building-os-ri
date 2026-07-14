import type { PointFreshness } from "@/lib/telemetry/freshness";
import { freshnessLabel } from "@/lib/telemetry/freshness-format";

// AA-contrast fills (mirrors the a11y pass on the admin tables): fresh = green, stale = amber, and
// missing = a neutral gray so a never-reporting point reads as "no data" rather than an alarm.
const STYLES: Record<PointFreshness["status"], string> = {
  fresh: "bg-green-100 text-green-800",
  stale: "bg-amber-100 text-amber-800",
  missing: "bg-gray-200 text-gray-700",
};

/** Small colored badge showing whether a point's latest sample is fresh / stale / missing. */
export function FreshnessBadge({ freshness }: { freshness: PointFreshness }) {
  return (
    <span
      data-testid={`freshness-${freshness.status}`}
      className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${STYLES[freshness.status]}`}
    >
      {freshnessLabel(freshness)}
    </span>
  );
}
