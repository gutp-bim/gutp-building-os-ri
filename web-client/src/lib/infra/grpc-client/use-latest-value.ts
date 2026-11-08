"use client";

import { TelemetryNotificationService } from "@/lib/gen/telemetry_notification_pb";
import type { TelemetryLatestSample } from "@/lib/telemetry/types";
import { ConnectError, createClient } from "@connectrpc/connect";
import { useEffect, useState } from "react";
import { authenticatedGrpcTransport } from "./index";
import {
  backoffMs,
  isFatalCode,
  toLatestSample,
  type LatestValueEventLike,
} from "./latest-value";

/**
 * - `connecting`: opening the stream (first time, or after a failure).
 * - `live`: the stream is open; the sample follows the point.
 * - `reconnecting`: the stream dropped; the last sample is shown but may be old.
 * - `unavailable`: the server refused (not signed in / not allowed / no such service) — no more retries.
 */
export type LatestValueStatus =
  "connecting" | "live" | "reconnecting" | "unavailable";

export type WatchLatest = (
  pointIds: string[],
  signal: AbortSignal,
) => AsyncIterable<LatestValueEventLike>;

const defaultWatch: WatchLatest = (pointIds, signal) =>
  createClient(
    TelemetryNotificationService,
    authenticatedGrpcTransport,
  ).watchLatest({ pointIds }, { signal });

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const id = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => {
      clearTimeout(id);
      resolve();
    });
  });
}

/**
 * Follows one point's latest value over the push stream (#636). The REST read stays the source of the first
 * paint and the fallback: this only adds the changes after it. Reconnects with exponential backoff; each
 * reconnect starts with a snapshot, so nothing changed while disconnected is missed.
 */
export function useLatestValue(
  pointId: string | null | undefined,
  { watch = defaultWatch }: { watch?: WatchLatest } = {},
): { sample: TelemetryLatestSample | null; status: LatestValueStatus } {
  const [sample, setSample] = useState<TelemetryLatestSample | null>(null);
  const [status, setStatus] = useState<LatestValueStatus>("connecting");

  useEffect(() => {
    if (!pointId) return;
    const abort = new AbortController();
    setSample(null);
    setStatus("connecting");

    (async () => {
      let failures = 0;
      while (!abort.signal.aborted) {
        try {
          for await (const e of watch([pointId], abort.signal)) {
            failures = 0;
            setStatus("live");
            if (e.pointId === pointId) setSample(toLatestSample(e));
          }
        } catch (err) {
          if (abort.signal.aborted) return;
          if (isFatalCode(err instanceof ConnectError ? err.code : undefined)) {
            setStatus("unavailable");
            return;
          }
        }
        if (abort.signal.aborted) return;
        // The stream ended or failed: show that the value may be old, then try again.
        failures += 1;
        setStatus("reconnecting");
        await sleep(backoffMs(failures), abort.signal);
      }
    })();

    return () => abort.abort();
  }, [pointId, watch]);

  return { sample, status };
}
