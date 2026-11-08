"use client";

import {
  ControlResult,
  PointControlService,
} from "@/lib/gen/point_control_pb";
import { createClient } from "@connectrpc/connect";
import { useCallback, useEffect, useRef, useState } from "react";
import { grpcTransport } from "./index";

/**
 * #637: whether the value actually changed after the gateway accepted the write. `confirming` while the server
 * watches the feedback point; then the outcome. Absent when the server sends no confirmation (an API that predates
 * #637), so the plain success is shown as before.
 */
export type ControlFeedbackState =
  | { kind: "confirming" }
  | { kind: "matched"; value?: string }
  | { kind: "mismatch"; value?: string }
  | { kind: "timeout" }
  | { kind: "unavailable"; reason?: string };

export type ControlExecutionState =
  | { status: "idle" }
  | { status: "executing"; controlId: string; elapsedSeconds: number }
  | { status: "success"; message?: string; feedback?: ControlFeedbackState }
  | { status: "failed"; message?: string }
  // 制御 POST が 403/503 で弾かれたときの説明つき失敗（#162）。ストリーム前に確定するため
  // setDirectResult 経由でのみ設定される。
  | { status: "permission_denied"; message?: string }
  | { status: "gateway_offline"; message?: string }
  | { status: "timeout" }
  | { status: "cancelled" };

const TIMEOUT_MS = 30_000;
const AUTO_DISMISS_MS = 5_000;

/** #637: a FEEDBACK_* result as the modal's confirmation state; null for any other result. */
export function toFeedbackState(
  result: ControlResult,
  value: string,
): ControlFeedbackState | null {
  const v = value.trim() === "" ? undefined : value;
  switch (result) {
    case ControlResult.FEEDBACK_MATCHED:
      return { kind: "matched", value: v };
    case ControlResult.FEEDBACK_MISMATCH:
      return { kind: "mismatch", value: v };
    case ControlResult.FEEDBACK_TIMEOUT:
      return { kind: "timeout" };
    case ControlResult.FEEDBACK_UNAVAILABLE:
      return { kind: "unavailable", reason: v };
    default:
      return null;
  }
}

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
      // Outside the try: a connection lost while waiting for the confirmation must not turn an accepted write
      // into "failed" (the operator might send it again).
      let succeeded = false;
      try {
        for await (const event of client.waitForResult(
          { controlId },
          { signal: abort.signal },
        )) {
          clearTimers();

          if (event.result === ControlResult.SUCCESS) {
            // #637: the confirmation follows on the same stream; keep reading for it.
            succeeded = true;
            setState({
              status: "success",
              message: event.response || undefined,
              feedback: { kind: "confirming" },
            });
            continue;
          }
          const feedback = succeeded ? toFeedbackState(event.result, event.feedbackValue) : null;
          if (feedback !== null) {
            setState((prev) =>
              prev.status === "success" ? { ...prev, feedback } : prev,
            );
            // A confirmed change can go away by itself; a value that did not change stays until dismissed.
            if (feedback.kind === "matched" || feedback.kind === "unavailable")
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
          break;
        }
        // The stream ended after a success without a confirmation (older API / bus expired): plain success.
        if (succeeded)
          setState((prev) =>
            prev.status === "success" && prev.feedback?.kind === "confirming"
              ? { status: "success", message: prev.message }
              : prev,
          );
        if (succeeded && !autoDismissRef.current)
          autoDismissRef.current = setTimeout(() => {
            setState((prev) =>
              prev.status === "success" &&
              (prev.feedback === undefined || prev.feedback.kind === "matched" || prev.feedback.kind === "unavailable")
                ? { status: "idle" }
                : prev,
            );
          }, AUTO_DISMISS_MS);
      } catch {
        if (succeeded) {
          // The write was accepted; only the confirmation is lost.
          setState((prev) =>
            prev.status === "success"
              ? {
                  ...prev,
                  feedback: { kind: "unavailable", reason: "connection_lost" },
                }
              : prev,
          );
          return;
        }
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
