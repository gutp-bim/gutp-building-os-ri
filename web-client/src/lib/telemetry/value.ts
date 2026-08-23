/**
 * Discriminated telemetry value (#152, ADR-0006). Numeric stays the primary type (charts/aggregation);
 * string and boolean are first-class state/status values. Charts remain numeric-only (see
 * {@link ./mapping.ts `toSeries`}); these helpers are for the single-latest surfaces that must show a
 * non-numeric reading as text.
 *
 * This module is the single place that decodes the wire's discriminated shape. Everything else in
 * `lib/telemetry/` and every component consumes {@link ResolvedTelemetryValue} instead — so when the
 * API eventually returns a union-typed `value` (#344), this file is the only one that changes.
 *
 * The aspida-generated `TelemetryReading`/`LatestSample` both structurally satisfy
 * {@link DiscriminatedTelemetryValue}, so they can be passed here directly — no cast needed.
 */
export type DiscriminatedTelemetryValue = {
  /**
   * The reading itself (#344): the API now returns one union-typed `value`, matching the canonical
   * schema and the NATS bus. `null` when the point has no representable reading.
   */
  value?: number | string | boolean | null;
  /**
   * "number" | "string" | "boolean" — the kind of `value` itself. The server derives it from the
   * value it ships rather than copying the stored tag, so it can no longer contradict `typeof value`
   * (it did for aggregate buckets, where the stored tag describes `valueText`). Kept for provenance
   * and for rows produced before #344; it is not needed to *find* the value.
   */
  valueType?: string | null;
  /** @deprecated Pre-#344 wire shape; still emitted this release, removed in #344 PR B. */
  valueText?: string | null;
  /** @deprecated Pre-#344 wire shape; still emitted this release, removed in #344 PR B. */
  valueBool?: boolean | null;
};

export type ResolvedTelemetryValue =
  | { kind: "number"; value: number }
  | { kind: "string"; value: string }
  | { kind: "boolean"; value: boolean }
  | { kind: "none" };

/**
 * Resolve a sample to the single union-typed value the API returns (#344): the reading itself.
 *
 * **Numeric first**, then `valueText`, then `valueBool` — the discriminant does not override it.
 * An *aggregate* row legitimately carries two values at once: the store sets `value` to the bucket
 * average (the continuous-aggregate contract) while tagging the bucket by its last-in-bucket
 * reading, so a mixed hour arrives as `{ value: 42, valueText: "auto" }` with `valueType: "number"`
 * describing the average. Collapsing that onto one value is lossy either way; the numeric half wins
 * here because `value` has a published numeric meaning at Hour/Day granularity that the chart
 * depends on. The state half is not lost — {@link resolveStateValue} is how the timeline reads it.
 *
 * For a *raw* row the question does not arise: the backend populates exactly one payload field, so
 * numeric-first and discriminant-first agree. `TelemetryValueKind.Resolve` applies the same
 * precedence; both sides are pinned by tests so they cannot drift.
 */
export function resolveTelemetryValue(
  v: DiscriminatedTelemetryValue,
): ResolvedTelemetryValue {
  if (typeof v.value === "number") return { kind: "number", value: v.value };
  if (typeof v.value === "string") return { kind: "string", value: v.value };
  if (typeof v.value === "boolean") return { kind: "boolean", value: v.value };
  // Pre-#344 servers put non-numeric readings here; aggregate rows always do.
  if (typeof v.valueText === "string")
    return { kind: "string", value: v.valueText };
  if (typeof v.valueBool === "boolean")
    return { kind: "boolean", value: v.valueBool };
  return { kind: "none" };
}

/**
 * Resolve a sample's **state representative** — the non-numeric reading a row carries, independent of
 * any numeric value beside it.
 *
 * This is deliberately not {@link resolveTelemetryValue}. A mixed aggregate bucket has both an
 * average and a state; asking the union resolver would return the average and the state timeline
 * would silently go empty for exactly those points. `valueText`/`valueBool` are checked first
 * because that is where both aggregate rows and pre-#344 servers put the representative; a
 * non-numeric union `value` covers the post-#344 raw row.
 */
export function resolveStateValue(
  v: DiscriminatedTelemetryValue,
): ResolvedTelemetryValue {
  if (typeof v.valueText === "string")
    return { kind: "string", value: v.valueText };
  if (typeof v.valueBool === "boolean")
    return { kind: "boolean", value: v.valueBool };
  if (typeof v.value === "string") return { kind: "string", value: v.value };
  if (typeof v.value === "boolean") return { kind: "boolean", value: v.value };
  return { kind: "none" };
}

/**
 * True when the sample carries a non-numeric state representative. Uses {@link resolveStateValue},
 * so a mixed aggregate bucket counts even though its union `value` is the numeric average.
 */
export function isNonNumericValue(v: DiscriminatedTelemetryValue): boolean {
  const r = resolveStateValue(v);
  return r.kind === "string" || r.kind === "boolean";
}

/**
 * Format a resolved value for display: numbers as-is, strings verbatim, booleans as ON/OFF. Returns
 * null when there is no representable value. Numeric callers that need scale/unit/enum-label formatting
 * should branch on {@link resolveTelemetryValue} instead — this is the plain state/text rendering
 * shared by the latest view and the state timeline.
 */
export function formatTelemetryValue(
  v: DiscriminatedTelemetryValue,
): string | null {
  return formatResolvedValue(resolveTelemetryValue(v));
}

/**
 * The same display formatting for a value that is already resolved — used by callers holding a
 * {@link ./types.ts `TelemetryLatestSample`}, so they do not re-derive the discriminant.
 */
export function formatResolvedValue(r: ResolvedTelemetryValue): string | null {
  switch (r.kind) {
    case "number":
      return String(r.value);
    case "string":
      return r.value;
    case "boolean":
      return r.value ? "ON" : "OFF";
    default:
      return null;
  }
}
