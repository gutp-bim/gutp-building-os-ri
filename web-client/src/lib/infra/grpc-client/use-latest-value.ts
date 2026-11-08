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

/** A stream that ran at least this long before ending cleanly was a planned end, not a failure. */
export const HEALTHY_STREAM_MS = 10_000;

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
): {
  sample: TelemetryLatestSample | null;
  /** The sample came from a (re)connect snapshot — a replay of what the server had, not a change. */
  fromSnapshot: boolean;
  status: LatestValueStatus;
} {
  const [pushed, setPushed] = useState<{
    sample: TelemetryLatestSample;
    fromSnapshot: boolean;
  } | null>(null);
  const [status, setStatus] = useState<LatestValueStatus>("connecting");

  useEffect(() => {
    if (!pointId) return;
    const abort = new AbortController();
    setPushed(null);
    setStatus("connecting");

    (async () => {
      let failures = 0;
      while (!abort.signal.aborted) {
        const openedAt = Date.now();
        let failed = false;
        try {
          for await (const e of watch([pointId], abort.signal)) {
            setStatus("live");
            if (e.pointId === pointId)
              setPushed({
                sample: toLatestSample(e),
                fromSnapshot: e.snapshot,
              });
          }
        } catch (err) {
          if (abort.signal.aborted) return;
          if (isFatalCode(err instanceof ConnectError ? err.code : undefined)) {
            setStatus("unavailable");
            return;
          }
          failed = true;
        }
        if (abort.signal.aborted) return;
        // Only a stream that stayed up a while resets the backoff — receiving the snapshot does not, or a
        // connection that drops right after every snapshot would retry every second forever.
        const healthy = Date.now() - openedAt >= HEALTHY_STREAM_MS;
        if (healthy) failures = 0;
        // The server ends a healthy stream on purpose after a while (so the caller is authorized again):
        // reconnect at once, without flashing "reconnecting". A stream that ends almost immediately is
        // treated as a failure so a misbehaving server cannot make this spin.
        if (!failed && healthy) continue;
        failures += 1;
        setStatus("reconnecting");
        await sleep(backoffMs(failures), abort.signal);
      }
    })();

    return () => abort.abort();
  }, [pointId, watch]);

  return {
    sample: pushed?.sample ?? null,
    fromSnapshot: pushed?.fromSnapshot ?? false,
    status,
  };
}
