import type { ServiceState } from "./types";

/** Normalises the raw status string to a known service state. */
export function toServiceState(status: string): ServiceState {
  switch (status) {
    case "up":
      return "up";
    case "down":
      return "down";
    default:
      return "unknown";
  }
}

/** Japanese label for a service state (display). */
export function serviceLabel(status: string): string {
  switch (toServiceState(status)) {
    case "up":
      return "稼働";
    case "down":
      return "停止";
    default:
      return "不明";
  }
}

/** Tailwind background class for the status dot. */
export function serviceDotClass(status: string): string {
  switch (toServiceState(status)) {
    case "up":
      return "bg-green-500";
    case "down":
      return "bg-red-500";
    default:
      return "bg-gray-400";
  }
}

/**
 * Formats a KPI value for a card. `null`/`undefined` (metrics backend unavailable or no data)
 * renders as an em dash so the card stays present but clearly "no value" rather than showing 0.
 */
export function formatKpi(
  value: number | null | undefined,
  options: { suffix?: string; maximumFractionDigits?: number } = {},
): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  const { suffix = "", maximumFractionDigits = 2 } = options;
  // Explicit locale (matches the rest of the web client) so KPI rendering is deterministic and not
  // dependent on the runtime default locale.
  return `${value.toLocaleString("ja-JP", { maximumFractionDigits })}${suffix}`;
}
