"use client";

import {
  ControlResult,
  PointControlService,
} from "@/lib/gen/point_control_pb";
import { createClient } from "@connectrpc/connect";
import { useCallback, useEffect, useRef, useState } from "react";
import { grpcTransport } from "./index";

export type ControlExecutionState =
  | { status: "idle" }
  | { status: "executing"; controlId: string; elapsedSeconds: number }
  | { status: "success"; message?: string }
  | { status: "failed"; message?: string }
  // 制御 POST が 403/503 で弾かれたときの説明つき失敗（#162）。ストリーム前に確定するため
  // setDirectResult 経由でのみ設定される。
  | { status: "permission_denied"; message?: string }
  | { status: "gateway_offline"; message?: string }
  | { status: "timeout" }
  | { status: "cancelled" };

const TIMEOUT_MS = 30_000;
const AUTO_DISMISS_MS = 5_000;

export function useControlExecution() {
  const [state, setState] = useState<ControlExecutionState>({ status: "idle" });
  const abortRef = useRef<AbortController | null>(null);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const autoDismissRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const clearTimers = useCallback(() => {
    if (timerRef.current) {
      clearInterval(timerRef.current);
      timerRef.current = null;
    }
    if (timeoutRef.current) {
      clearTimeout(timeoutRef.current);
      timeoutRef.current = null;
    }
  }, []);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      abortRef.current?.abort();
      clearTimers();
      if (autoDismissRef.current) clearTimeout(autoDismissRef.current);
    };
  }, [clearTimers]);

  const startExecution = useCallback(
    async (controlId: string) => {
      // Cleanup any previous execution
      abortRef.current?.abort();
      clearTimers();
      if (autoDismissRef.current) {
        clearTimeout(autoDismissRef.current);
        autoDismissRef.current = null;
      }

      const abort = new AbortController();
      abortRef.current = abort;

      // Start elapsed timer
      const startTime = Date.now();
      setState({ status: "executing", controlId, elapsedSeconds: 0 });
      timerRef.current = setInterval(() => {
        setState((prev) => {
          if (prev.status !== "executing") return prev;
          return {
            ...prev,
            elapsedSeconds: Math.floor((Date.now() - startTime) / 1000),
          };
        });
      }, 1000);

      // Client-side timeout
      let timedOut = false;
      timeoutRef.current = setTimeout(() => {
        timedOut = true;
        abort.abort();
        clearTimers();
        setState({ status: "timeout" });
      }, TIMEOUT_MS);

      // Start gRPC stream
      const client = createClient(PointControlService, grpcTransport);
      try {
        for await (const event of client.waitForResult(
          { controlId },
          { signal: abort.signal },
        )) {
          clearTimers();

          if (event.result === ControlResult.SUCCESS) {
            setState({
              status: "success",
              message: event.response || undefined,
            });
            autoDismissRef.current = setTimeout(() => {
              setState({ status: "idle" });
            }, AUTO_DISMISS_MS);
          } else if (event.result === ControlResult.FAILED) {
            setState({
              status: "failed",
              message: event.response || undefined,
            });
          } else {
            setState({ status: "failed", message: "不明な結果を受信しました" });
          }
          break; // 1件のみ
        }
      } catch {
        if (abort.signal.aborted) {
          // timeout handler が既に state を設定済みか判定
          if (!timedOut) {
            setState({ status: "cancelled" });
          }
        } else {
          clearTimers();
          setState({
            status: "failed",
            message: "接続エラーが発生しました",
          });
        }
      }
    },
    [clearTimers],
  );

  const cancel = useCallback(() => {
    abortRef.current?.abort();
    clearTimers();
    setState({ status: "cancelled" });
  }, [clearTimers]);

  const dismiss = useCallback(() => {
    if (autoDismissRef.current) {
      clearTimeout(autoDismissRef.current);
      autoDismissRef.current = null;
    }
    setState({ status: "idle" });
  }, []);

  /** POST 失敗時にストリームなしで直接結果をセット（汎用失敗 / 権限不足 / gateway offline） */
  const setDirectResult = useCallback(
    (
      status: "failed" | "permission_denied" | "gateway_offline",
      message: string,
    ) => {
      clearTimers();
      setState({ status, message });
    },
    [clearTimers],
  );

  const isExecuting = state.status === "executing";

  return { state, startExecution, cancel, dismiss, setDirectResult, isExecuting };
}
