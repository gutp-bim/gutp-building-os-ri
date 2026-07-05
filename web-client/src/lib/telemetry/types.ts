/**
 * Domain types for telemetry, decoupled from the aspida `ValidTelemetryData`. UI reads these via
 * `lib/telemetry/repository.ts`, which routes everything through the unified `/telemetries/query`
 * endpoint (tier auto-selection) so the hot/warm/cold split stays an implementation detail.
 */

export type Granularity = "raw" | "hour" | "day";

/** One sample: ISO-8601 timestamp + numeric value. */
export type TelemetryPoint = { t: string; v: number };

export type TelemetrySeries = {
  pointId: string;
  points: TelemetryPoint[];
};

export type TelemetryQuery = {
  pointId: string;
  start?: Date;
  end?: Date;
  granularity?: Granularity;
  latest?: boolean;
};
