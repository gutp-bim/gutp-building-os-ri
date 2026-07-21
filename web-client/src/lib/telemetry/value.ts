/**
 * Discriminated telemetry value (#152, ADR-0006). Numeric stays the primary type (charts/aggregation);
 * string and boolean are first-class state/status values. Charts remain numeric-only (see
 * {@link ./mapping.ts `toSeries`}); these helpers are for the single-latest surfaces that must show a
 * non-numeric reading as text.
 *
 * The aspida-generated `ValidTelemetryData`/`LatestSample` do not yet carry `valueType`/`valueText`/
 * `valueBool` — regenerating them needs `./sync-type.bash` against a freshly-generated Swagger, a
 * follow-up. Until then this type bridges the gap (a structural mirror of the backend records), the
 * same pattern used for the #158 alarm thresholds.
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
 * present; otherwise it is inferred from whichever field is populated (numeric first, matching the
 * legacy "absent valueType → number" default). Returns `{ kind: "none" }` when nothing is representable.
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
