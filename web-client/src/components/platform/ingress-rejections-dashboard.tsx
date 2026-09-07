"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { fetchIngressRejectionStats } from "@/lib/ingress-rejections/fetch-ingress-rejections";
import type { IngressRejectionStats } from "@/lib/ingress-rejections/types";
import { IngressRejectionsView } from "./ingress-rejections-view";

const REFRESH_INTERVAL_MS = 15_000;

/**
 * Client wrapper for the platform ingress-rejections screen (#292): fetches
 * `/api/system/ingress-rejections` on mount and auto-refreshes every 15s, rendering the pure
 * {@link IngressRejectionsView}. Keeps showing the last good snapshot while a refresh is in flight
 * or errors, so a transient blip does not blank the screen.
 */
export function IngressRejectionsDashboard() {
  const [stats, setStats] = useState<IngressRejectionStats | null>(null);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false);
  const mounted = useRef(true);

  const load = useCallback(async (signal?: AbortSignal) => {
    if (inFlight.current) return;
    inFlight.current = true;
    try {
      const next = await fetchIngressRejectionStats(signal);
      if (!mounted.current) return;
      setStats(next);
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

  if (stats === null) {
    return (
      <div className="container mx-auto px-4 py-8">
        {error ? (
          <p className="text-red-600" data-testid="ingress-rejections-error">
            拒否件数の取得に失敗しました: {error}
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
        <p
          className="container mx-auto px-4 pt-4 text-sm text-red-600"
          data-testid="ingress-rejections-error"
        >
          最新化に失敗しました（前回値を表示中）: {error}
        </p>
      ) : null}
      <IngressRejectionsView stats={stats} updatedAt={updatedAt} />
    </>
  );
}
