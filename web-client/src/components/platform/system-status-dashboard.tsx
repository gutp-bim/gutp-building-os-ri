"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { fetchSystemStatus } from "@/lib/system-status/fetch-status";
import type { SystemStatus } from "@/lib/system-status/types";
import { SystemStatusView } from "./system-status-view";

const REFRESH_INTERVAL_MS = 15_000;
const GRAFANA_URL = process.env.NEXT_PUBLIC_GRAFANA_URL || null;

/**
 * Client wrapper for the platform status screen (#146): fetches `/api/system/status` on mount and
 * auto-refreshes every 15s, rendering the pure {@link SystemStatusView}. Keeps showing the last good
 * snapshot while a refresh is in flight or errors, so a transient blip does not blank the screen.
 */
export function SystemStatusDashboard() {
  const [status, setStatus] = useState<SystemStatus | null>(null);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false);
  const mounted = useRef(true);

  const load = useCallback(async (signal?: AbortSignal) => {
    if (inFlight.current) return;
    inFlight.current = true;
    try {
      const next = await fetchSystemStatus(signal);
      if (!mounted.current) return;
      setStatus(next);
      setUpdatedAt(new Date());
      setError(null);
    } catch (e) {
      if ((e as Error)?.name === "AbortError" || !mounted.current) return;
      setError((e as Error).message);
    } finally {
      inFlight.current = false;
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    void load(controller.signal);
    const id = setInterval(() => void load(controller.signal), REFRESH_INTERVAL_MS);
    return () => {
      mounted.current = false;
      clearInterval(id);
      controller.abort();
    };
  }, [load]);

  if (status === null) {
    return (
      <div className="container mx-auto px-4 py-8">
        {error ? (
          <p className="text-red-600" data-testid="status-error">
            稼働状態の取得に失敗しました: {error}
          </p>
        ) : (
          <p className="text-gray-600">読み込み中…</p>
        )}
      </div>
    );
  }

  return (
    <>
      {error ? (
        <p className="container mx-auto px-4 pt-4 text-sm text-red-600" data-testid="status-error">
          最新化に失敗しました（前回値を表示中）: {error}
        </p>
      ) : null}
      <SystemStatusView status={status} grafanaUrl={GRAFANA_URL} updatedAt={updatedAt} />
    </>
  );
}
