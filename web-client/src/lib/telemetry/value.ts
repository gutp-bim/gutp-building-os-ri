/**
 * The value-carrying fields of a telemetry wire row, and the single place they are decoded (#152,
 * ADR-0006 → #344 → #359). Numeric stays the primary type (charts/aggregation); string and boolean
 * are first-class state/status values. Charts remain numeric-only (see {@link ./mapping.ts
 * `toSeries`}); these helpers serve the state timeline and the single-latest surfaces that must show
 * a non-numeric reading as text.
 *
 * Everything else in `lib/telemetry/` and every component consumes {@link ResolvedTelemetryValue}
 * instead, so a change to the wire's value model lands here and nowhere else.
 *
 * The aspida-generated `TelemetryReading`/`LatestSample` both structurally satisfy
 * {@link TelemetryWireValue}, so they can be passed here directly — no cast needed.
 */
export type TelemetryWireValue = {
  /**
   * The reading itself (#344): one union-typed `value`, matching the canonical schema and the NATS
   * bus. `null` when the point has no representable reading.
   */
  value?: number | string | boolean | null;
  /**
   * "number" | "string" | "boolean" — the kind of `value` itself. The server derives it from the
   * value it ships rather than copying the stored tag, so it cannot contradict `typeof value`. It is
   * a descriptor, not a lookup key: nothing here branches on it to *find* the reading.
   */
  valueType?: string | null;
  /**
   * The reading's non-numeric half (#359), independent of any number in `value`.
   *
   * It exists for the one row shape carrying two readings at once: an aggregate bucket sets `value`
   * to the average unconditionally, so a mixed hour ships the number while its last-in-bucket state
   * needs somewhere to live. A raw non-numeric row repeats its reading here, which is what lets
   * {@link resolveStateValue} be a single lookup instead of a fallback chain.
   *
   * Replaced the pre-#359 `valueText`/`valueBool` pair. A server older than #359 sends no `state`
   * at all, so against one {@link resolveStateValue} returns nothing for *every* row and the state
   * timeline goes blank at every granularity — **deploy the API server before this client**. See
   * the skew tests in `value.test.ts` for both directions.
   */
  state?: string | boolean | null;
};

export type ResolvedTelemetryValue =
  | { kind: "number"; value: number }
  | { kind: "string"; value: string }
  | { kind: "boolean"; value: boolean }
  | { kind: "none" };

/**
 * Resolve a sample to the single union-typed value the API returns (#344): the reading itself.
 *
 * Reads `value` and nothing else. An *aggregate* row legitimately carries two readings at once — the
 * store sets `value` to the bucket average (the continuous-aggregate contract) while a mixed hour
 * also has a last-in-bucket state — and the numeric half is the one that belongs here, because
 * `value` has a published numeric meaning at Hour/Day granularity that the chart depends on. The
 * state half is not lost; {@link resolveStateValue} is how the timeline reads it.
 *
 * `TelemetryValueKind.Resolve` produces exactly this on the server side; both are pinned by tests so
 * they cannot drift.
 */
export function resolveTelemetryValue(
  v: TelemetryWireValue,
): ResolvedTelemetryValue {
  if (typeof v.value === "number") return { kind: "number", value: v.value };
  if (typeof v.value === "string") return { kind: "string", value: v.value };
  if (typeof v.value === "boolean") return { kind: "boolean", value: v.value };
  return { kind: "none" };
}

/**
 * Resolve a sample's **state representative** — the non-numeric reading a row carries, independent of
 * any numeric value beside it.
 *
 * This is deliberately not {@link resolveTelemetryValue}. A mixed aggregate bucket has both an
 * average and a state; asking the union resolver would return the average and the state timeline
 * would silently go empty for exactly those points.
 *
 * A single lookup with no fallback, because the server populates `state` for raw non-numeric rows
 * too even though it duplicates `value` there (#359). `typeof` rather than a truthiness check:
 * `false` is a reading, and treating it as absent would drop every OFF sample from the timeline.
 */
export function resolveStateValue(
  v: TelemetryWireValue,
): ResolvedTelemetryValue {
  if (typeof v.state === "string") return { kind: "string", value: v.state };
  if (typeof v.state === "boolean") return { kind: "boolean", value: v.state };
  return { kind: "none" };
}

/**
 * Display formatting for a resolved value: numbers as-is, strings verbatim, booleans as ON/OFF.
 * Returns null when there is no representable value. Numeric callers that need scale/unit/enum-label
 * formatting should branch on the {@link ResolvedTelemetryValue} themselves — this is the plain
 * state/text rendering shared by the latest view and the state timeline.
 *
 * There is deliberately no wire-shape overload. `isNonNumericValue` and `formatTelemetryValue` used
 * to take a {@link TelemetryWireValue} and resolve it inline; both were removed in #359 with zero
 * production callers. Keeping them would have meant maintaining a tested API whose meaning had
 * quietly shifted under the field rename — `isNonNumericValue` in particular read as "the value is
 * non-numeric" while it answered "this row has a state half", which is a different question for a
 * mixed aggregate bucket. Resolve first, then format.
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
