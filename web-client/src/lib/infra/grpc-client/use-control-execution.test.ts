import { ControlResult } from "@/lib/gen/point_control_pb";
import { act, renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { toFeedbackState, useControlExecution } from "./use-control-execution";

describe("toFeedbackState (#637)", () => {
  it("maps each FEEDBACK_* result, keeping the value or reason", () => {
    expect(toFeedbackState(ControlResult.FEEDBACK_MATCHED, "24")).toEqual({
      kind: "matched",
      value: "24",
    });
    expect(toFeedbackState(ControlResult.FEEDBACK_MISMATCH, "22")).toEqual({
      kind: "mismatch",
      value: "22",
    });
    expect(toFeedbackState(ControlResult.FEEDBACK_TIMEOUT, "")).toEqual({
      kind: "timeout",
    });
    expect(
      toFeedbackState(ControlResult.FEEDBACK_UNAVAILABLE, "no_feedback_point"),
    ).toEqual({
      kind: "unavailable",
      reason: "no_feedback_point",
    });
  });

  it("an empty value is absent, and a non-feedback result is not a confirmation", () => {
    expect(toFeedbackState(ControlResult.FEEDBACK_MATCHED, " ")).toEqual({
      kind: "matched",
      value: undefined,
    });
    expect(toFeedbackState(ControlResult.SUCCESS, "")).toBeNull();
    expect(toFeedbackState(ControlResult.FAILED, "")).toBeNull();
  });
});

// ── #637: the stream stays open after SUCCESS; a dismissed one must not touch a newer result ──

const streams = vi.hoisted(() => ({
  next: [] as Array<(signal: AbortSignal) => AsyncIterable<unknown>>,
}));
vi.mock("@connectrpc/connect", async (orig) => ({
  ...(await orig<typeof import("@connectrpc/connect")>()),
  createClient: () => ({
    waitForResult: (_req: unknown, opts: { signal: AbortSignal }) =>
      streams.next.shift()!(opts.signal),
  }),
}));

function successThenWait(feedbackAfterMs: number) {
  return async function* (signal: AbortSignal) {
    yield { result: ControlResult.SUCCESS, response: "", feedbackValue: "" };
    await new Promise<void>((resolve, reject) => {
      const id = setTimeout(resolve, feedbackAfterMs);
      signal.addEventListener("abort", () => {
        clearTimeout(id);
        reject(new DOMException("aborted", "AbortError"));
      });
    });
    yield {
      result: ControlResult.FEEDBACK_MATCHED,
      response: "",
      feedbackValue: "24",
    };
  };
}

describe("useControlExecution confirmation lifecycle (#637)", () => {
  it("shows confirming after SUCCESS, then the outcome", async () => {
    streams.next.push(successThenWait(300));
    const { result } = renderHook(() => useControlExecution());

    act(() => {
      void result.current.startExecution("c1");
    });
    await waitFor(() =>
      expect(result.current.state).toMatchObject({
        status: "success",
        feedback: { kind: "confirming" },
      }),
    );
    await waitFor(() =>
      expect(result.current.state).toMatchObject({
        status: "success",
        feedback: { kind: "matched", value: "24" },
      }),
    );
  });

  it("a dismissed confirmation cannot overwrite a newer result", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    streams.next.push(successThenWait(50));
    const { result } = renderHook(() => useControlExecution());

    act(() => {
      void result.current.startExecution("c1");
    });
    await waitFor(() => expect(result.current.state.status).toBe("success"));
    act(() => result.current.dismiss());
    act(() => result.current.setDirectResult("gateway_offline", "offline"));

    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(result.current.state).toMatchObject({ status: "gateway_offline" });
    vi.useRealTimers();
  });
});
