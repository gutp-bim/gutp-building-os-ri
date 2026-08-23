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
   * "number" | "string" | "boolean". Retained for provenance and for rows produced before #344; it
   * is no longer needed to *find* the value, only to reject one whose type contradicts it.
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
 * Resolve a sample to a single typed variant.
 *
 * Since #344 the wire carries one union-typed `value`, so `typeof value` *is* the discriminant and
 * `valueType` is demoted to a **reject-only** confirmation: it can veto a value whose runtime type
 * contradicts it, but it is never needed to locate one. The backend resolver
 * (`TelemetryValueKind.Resolve`) applies the same precedence — the two must agree, which is why both
 * are pinned by tests.
 *
 * The `valueText`/`valueBool` branch is the pre-#344 compatibility path. The API dual-emits this
 * release, so it only fires for a response produced by an older server (or a hand-built fixture);
 * it goes away with those fields in #344 PR B. Returns `{ kind: "none" }` when nothing is
 * representable.
 */
export function resolveTelemetryValue(
  v: DiscriminatedTelemetryValue,
): ResolvedTelemetryValue {
  const type = v.valueType ?? null;
  const value = v.value;

  // The union path: the runtime type decides, unless the discriminant explicitly denies it.
  if (typeof value === "string") {
    return type === null || type === "string" ? { kind: "string", value } : { kind: "none" };
  }
  if (typeof value === "boolean") {
    return type === null || type === "boolean" ? { kind: "boolean", value } : { kind: "none" };
  }
  if (typeof value === "number") {
    return type === null || type === "number" ? { kind: "number", value } : { kind: "none" };
  }

  // Pre-#344 fallback: `value` was numeric-only and non-numeric readings rode in these fields.
  if (typeof v.valueText === "string" && (type === null || type === "string")) {
    return { kind: "string", value: v.valueText };
  }
  if (typeof v.valueBool === "boolean" && (type === null || type === "boolean")) {
    return { kind: "boolean", value: v.valueBool };
  }
  return { kind: "none" };
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
