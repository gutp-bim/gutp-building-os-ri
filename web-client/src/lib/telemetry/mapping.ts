import type {
  LatestSample,
  TelemetryGranularity,
  TelemetryReading,
} from "@/lib/infra/aspida-client/generated/@types";
import type { PointLastSeen } from "./freshness";
import type {
  Granularity,
  TelemetryLatestSample,
  TelemetryPoint,
  TelemetrySeries,
  TelemetryStatePoint,
  TelemetryStateSeries,
} from "./types";
import { formatResolvedValue, resolveStateValue, resolveTelemetryValue } from "./value";

// Backend enum ordinals (BuildingOS.Shared.Infrastructure.Telemetry.TelemetryGranularity):
// Raw = 0, Hour = 1, Day = 2. OpenAPI/aspida types this as a bare number, so the friendly
// lowercase names used across the UI are mapped to their ordinal here, once.
const GRANULARITY_ORDINAL: Record<Granularity, TelemetryGranularity> = {
  raw: 0,
  hour: 1,
  day: 2,
};

/**
 * Pure conversion of a `latest=true` result set into the domain {@link TelemetryLatestSample} — the
 * last row's discriminated value resolved once, so the single-latest UI surfaces never touch the
 * wire shape. Returns null when there is no row at all (a row that carries no representable value
 * still comes back, with `{ kind: "none" }`, because its timestamp is meaningful for freshness).
 */
export function toLatestSample(
  data: TelemetryReading[],
): TelemetryLatestSample | null {
  const last = data.at(-1);
  if (last === undefined) return null;
  return { t: last.datetime ?? null, value: resolveTelemetryValue(last) };
}

/**
 * Pure conversion of `batch-latest` rows (#182) into the freshness/alarm domain shape. Rows without a
 * `pointId` are dropped — the server omits points a non-admin cannot read, and the caller fills those
 * in as missing.
 *
 * `value` stays **numeric-only** by design: it feeds the threshold comparison in
 * {@link ./alarm.ts `classifyPointAlarms`}, which has no meaning for a string/boolean reading (see
 * `PointLastSeen.value`). But the discriminant decides which readings qualify — a row tagged
 * string/boolean yields `null` even when a stale numeric `value` rides along, so the evaluator never
 * compares a number that is not the reading.
 */
export function toPointsLastSeen(rows: LatestSample[]): PointLastSeen[] {
  return rows.flatMap((r) => {
    if (typeof r.pointId !== "string") return [];
    const resolved = resolveTelemetryValue(r);
    return [
      {
        pointId: r.pointId,
        lastSeen: r.datetime ?? null,
        value: resolved.kind === "number" ? resolved.value : null,
      },
    ];
  });
}

export function toGranularityParam(
  granularity: Granularity | undefined,
): TelemetryGranularity | undefined {
  return granularity === undefined
    ? undefined
    : GRANULARITY_ORDINAL[granularity];
}

/**
 * Pure conversion of raw telemetry rows into a clean, datetime-ascending series. Rows without a
 * datetime, and rows whose resolved value is not numeric, are dropped; a zero value is kept (it is a
 * real reading, not "missing").
 *
 * The value goes through {@link ./value.ts `resolveTelemetryValue`} rather than a bare
 * `typeof d.value === "number"`, so the discriminant decides: a row tagged string/boolean is dropped
 * even if a stale numeric `value` rides along. That makes this the exact complement of
 * {@link toStateSeries} — every row lands in one series or neither, never both.
 */
export function toSeries(
  pointId: string,
  data: TelemetryReading[],
): TelemetrySeries {
  const points: TelemetryPoint[] = data
    .flatMap((d) => {
      if (typeof d.datetime !== "string") return [];
      const resolved = resolveTelemetryValue(d);
      return resolved.kind === "number"
        ? [{ t: d.datetime, v: resolved.value }]
        : [];
    })
    .sort((a, b) => new Date(a.t).getTime() - new Date(b.t).getTime());

  return { pointId, points };
}

/**
 * Pure conversion of raw telemetry rows into a datetime-ascending NON-numeric state series (#152
 * Phase B). Numeric rows are dropped (they belong on the chart via {@link toSeries}); each string/
 * boolean row becomes a display `state` string (booleans → ON/OFF). Rows without a datetime are
 * dropped.
 */
export function toStateSeries(
  pointId: string,
  data: TelemetryReading[],
): TelemetryStateSeries {
  const points: TelemetryStatePoint[] = data
    .flatMap((d) => {
      if (typeof d.datetime !== "string") return [];
      // The state half, not the union: a mixed aggregate bucket's union `value` is the numeric
      // average, but the bucket still has a state representative that belongs on this timeline.
      const resolved = resolveStateValue(d);
      if (resolved.kind !== "string" && resolved.kind !== "boolean") return [];
      return [{ t: d.datetime, state: formatResolvedValue(resolved) ?? "" }];
    })
    .sort((a, b) => new Date(a.t).getTime() - new Date(b.t).getTime());

  return { pointId, points };
}
