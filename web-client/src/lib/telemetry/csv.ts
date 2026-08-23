import type { TelemetryPoint, TelemetryStatePoint } from "./types";

/**
 * CSV for the telemetry download on the point detail screen.
 *
 * The button hands the operator the telemetry they are currently looking at, so this takes **both**
 * halves of what the trend view renders: the numeric series behind the chart and the state series
 * behind the timeline. Taking only the numeric half is what made a non-numeric point download as a
 * bare header — no rows, no error, indistinguishable from "no data in range".
 *
 * Timestamps stay in the viewer's locale and timezone (`toLocaleString("ja-JP")`) on purpose: this
 * file is the chart the operator is looking at, and the chart is in their local time. It is not an
 * interchange format.
 */
export type TelemetryCsvInput = {
  series: TelemetryPoint[];
  state: TelemetryStatePoint[];
};

/** Excel decodes a BOM-less UTF-8 file as the system codepage, mangling the Japanese header. */
const BOM = "﻿";

const HEADER = "日時,値";

/**
 * RFC 4180 quoting. Only needed since state strings can reach this file — a value containing a
 * comma would otherwise shift every column after it.
 */
function escapeCsv(value: string): string {
  return /[",\n\r]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

export function toTelemetryCsv({ series, state }: TelemetryCsvInput): string {
  const rows = [
    ...series.map((p) => ({ t: p.t, value: String(p.v) })),
    ...state.map((p) => ({ t: p.t, value: p.state })),
  ].sort((a, b) => new Date(a.t).getTime() - new Date(b.t).getTime());

  const lines = rows.map(
    (r) =>
      `${escapeCsv(new Date(r.t).toLocaleString("ja-JP"))},${escapeCsv(r.value)}`,
  );

  return BOM + [HEADER, ...lines].join("\n");
}
