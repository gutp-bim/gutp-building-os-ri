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

/**
 * Excel decodes a BOM-less UTF-8 file as the system codepage, mangling the Japanese header. Written
 * as an escape rather than the literal character so it survives review, copy/paste and formatters —
 * a raw U+FEFF is invisible in a diff.
 */
const BOM = "\uFEFF";

const HEADER = "日時,値";

/**
 * Excel (and Sheets, and LibreOffice) evaluates a cell whose text begins with `=`, `+`, `-` or `@`
 * as a formula. State strings are twin-supplied data landing in a spreadsheet, so they have to be
 * neutralised; RFC 4180 quoting does not help, because the reader strips the quotes first and then
 * evaluates what is inside.
 *
 * Numbers are exempt: a reading of `-3.5` is not a formula, and prefixing it would corrupt the
 * value for the one audience this file has.
 */
function neutralizeFormula(value: string): string {
  if (/^-?\d+(\.\d+)?$/.test(value)) return value;
  return /^[=+\-@]/.test(value) ? `'${value}` : value;
}

/**
 * RFC 4180 quoting. Only needed since state strings can reach this file — a value containing a
 * comma would otherwise shift every column after it.
 */
function escapeCsv(value: string): string {
  return /[",\n\r]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

const cell = (value: string) => escapeCsv(neutralizeFormula(value));

export function toTelemetryCsv({ series, state }: TelemetryCsvInput): string {
  // The timestamp is parsed once per row rather than on every comparison — both inputs already
  // arrive datetime-ascending from `toSeries`/`toStateSeries`, so this only merges two sorted lists,
  // but a long export makes the difference visible.
  const byTime = new Map<number, { t: string; value: string }>();

  // State first, numeric second: an aggregate bucket carries a numeric average AND a state
  // representative, so both inputs can hold the same timestamp. Two columns cannot express both, and
  // emitting two rows with one timestamp and contradictory values is worse than picking one — so the
  // numeric write lands last and wins, matching `resolveTelemetryValue`'s numeric-first precedence.
  // (The download forces raw, where the two series are true complements, so this is a guard rather
  // than a shape the screen produces.)
  for (const p of state)
    byTime.set(new Date(p.t).getTime(), { t: p.t, value: p.state });
  for (const p of series)
    byTime.set(new Date(p.t).getTime(), { t: p.t, value: String(p.v) });

  const rows = [...byTime.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([, row]) => row);

  const lines = rows.map(
    (r) => `${cell(new Date(r.t).toLocaleString("ja-JP"))},${cell(r.value)}`,
  );

  return BOM + [HEADER, ...lines].join("\n");
}
