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
 * The aspida-generated `ValidTelemetryData`/`LatestSample` both structurally satisfy
 * {@link DiscriminatedTelemetryValue} (they carry all four fields since `1e755b9`), so they can be
 * passed here directly — no cast needed.
 */
export type DiscriminatedTelemetryValue = {
  value?: number | null;
  /** "number" | "string" | "boolean"; absent/null → numeric (legacy data, #152 D2). */
  valueType?: string | null;
  valueText?: string | null;
  valueBool?: boolean | null;
};

export type ResolvedTelemetryValue =
  | { kind: "number"; value: number }
  | { kind: "string"; value: string }
  | { kind: "boolean"; value: boolean }
  | { kind: "none" };

/**
 * Resolve a sample's discriminated value to a single typed variant. The discriminant is trusted when
 * present; otherwise precedence is `valueText` → `valueBool` → the legacy numeric default.
 *
 * That order is deliberate: `TelemetryValueKind.Apply` (backend) always sets `ValueType` and exactly
 * one payload field, so the only rows arriving without a discriminant are pre-#152 legacy ones — and
 * those carry no `valueText`/`valueBool` at all. A populated text/bool field is therefore the
 * stronger signal, and "absent valueType → number" only ever meant "absent valueType with just
 * `value` populated". Returns `{ kind: "none" }` when nothing is representable.
 */
export function resolveTelemetryValue(
  v: DiscriminatedTelemetryValue,
): ResolvedTelemetryValue {
  const type = v.valueType ?? null;

  if (type === "string" || (type === null && typeof v.valueText === "string")) {
    return typeof v.valueText === "string"
      ? { kind: "string", value: v.valueText }
      : { kind: "none" };
  }
  if (type === "boolean" || (type === null && typeof v.valueBool === "boolean")) {
    return typeof v.valueBool === "boolean"
      ? { kind: "boolean", value: v.valueBool }
      : { kind: "none" };
  }
  // Numeric (explicit "number" or the legacy default).
  return typeof v.value === "number"
    ? { kind: "number", value: v.value }
    : { kind: "none" };
}

/** True when the sample carries a non-numeric (string/boolean) first-class value. */
export function isNonNumericValue(v: DiscriminatedTelemetryValue): boolean {
  const r = resolveTelemetryValue(v);
  return r.kind === "string" || r.kind === "boolean";
}

/**
 * Format a resolved value for display: numbers as-is, strings verbatim, booleans as ON/OFF. Returns
 * null when there is no representable value. Numeric callers that need scale/unit/enum-label formatting
 * should branch on {@link resolveTelemetryValue} instead — this is the plain state/text rendering
 * shared by the latest view and the state timeline.
 */
export function formatTelemetryValue(v: DiscriminatedTelemetryValue): string | null {
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
