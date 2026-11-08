import { Code, ConnectError } from "@connectrpc/connect";
import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { LatestValueEventLike } from "./latest-value";
import {
  HEALTHY_STREAM_MS,
  useLatestValue,
  type WatchLatest,
} from "./use-latest-value";

const num = (
  value: number,
  datetime: string,
  snapshot = false,
): LatestValueEventLike => ({
  pointId: "P1",
  value: { case: "numberValue", value },
  datetime,
  snapshot,
});

/** A stream the test drives: push events, then end it or fail it. */
function controllableStream() {
  const queue: (LatestValueEventLike | Error | "end")[] = [];
  let wake: (() => void) | null = null;
  const push = (x: LatestValueEventLike | Error | "end") => {
    queue.push(x);
    wake?.();
  };
  async function* iterate(
    signal: AbortSignal,
  ): AsyncIterable<LatestValueEventLike> {
    while (!signal.aborted) {
      if (queue.length === 0) {
        await new Promise<void>((r) => {
          wake = r;
          signal.addEventListener("abort", () => r());
        });
        continue;
      }
      const next = queue.shift()!;
      if (next === "end") return;
      if (next instanceof Error) throw next;
      yield next;
    }
  }
  return { push, iterate };
}

afterEach(() => vi.useRealTimers());

describe("useLatestValue (#636)", () => {
  it("is live after the snapshot and follows later changes", async () => {
    const s = controllableStream();
    const watch: WatchLatest = vi.fn((_ids, signal) => s.iterate(signal));
    const { result } = renderHook(() => useLatestValue("P1", { watch }));

    expect(result.current.status).toBe("connecting");
    act(() => s.push(num(1, "2026-11-08T00:00:00Z", true)));
    await waitFor(() => expect(result.current.status).toBe("live"));
    expect(result.current.sample?.value).toEqual({ kind: "number", value: 1 });
    expect(result.current.fromSnapshot).toBe(true);

    act(() => s.push(num(2, "2026-11-08T00:00:10Z")));
    await waitFor(() =>
      expect(result.current.sample?.value).toEqual({
        kind: "number",
        value: 2,
      }),
    );
    expect(result.current.fromSnapshot).toBe(false);
    expect(watch).toHaveBeenCalledWith(["P1"], expect.any(AbortSignal));
  });

  it("shows reconnecting when the stream drops, then opens it again", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const first = controllableStream();
    const second = controllableStream();
    const streams = [first, second];
    const watch: WatchLatest = vi.fn((_ids, signal) =>
      streams.shift()!.iterate(signal),
    );
    const { result } = renderHook(() => useLatestValue("P1", { watch }));

    act(() => first.push(num(1, "2026-11-08T00:00:00Z", true)));
    await waitFor(() => expect(result.current.status).toBe("live"));
    act(() => first.push(new ConnectError("dropped", Code.Unavailable)));
    await waitFor(() => expect(result.current.status).toBe("reconnecting"));

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_000);
    });
    act(() => second.push(num(3, "2026-11-08T00:01:00Z", true)));
    await waitFor(() => expect(result.current.status).toBe("live"));
    expect(result.current.sample?.value).toEqual({ kind: "number", value: 3 });
    expect(watch).toHaveBeenCalledTimes(2);
  });

  it("reconnects at once, staying live, when a long-lived stream ends on purpose", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const first = controllableStream();
    const second = controllableStream();
    const streams = [first, second];
    const watch: WatchLatest = vi.fn((_ids, signal) =>
      streams.shift()!.iterate(signal),
    );
    const seen: string[] = [];
    const { result } = renderHook(() => {
      const r = useLatestValue("P1", { watch });
      seen.push(r.status);
      return r;
    });

    act(() => first.push(num(1, "2026-11-08T00:00:00Z", true)));
    await waitFor(() => expect(result.current.status).toBe("live"));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(HEALTHY_STREAM_MS);
    });
    act(() => first.push("end")); // the server's lifetime is up

    await waitFor(() => expect(watch).toHaveBeenCalledTimes(2));
    expect(result.current.status).toBe("live");
    expect(seen).not.toContain("reconnecting");
  });

  it("treats a stream that ends right away as a failure (no tight loop)", async () => {
    const s = controllableStream();
    const watch: WatchLatest = vi.fn((_ids, signal) => s.iterate(signal));
    const { result } = renderHook(() => useLatestValue("P1", { watch }));

    act(() => s.push("end"));

    await waitFor(() => expect(result.current.status).toBe("reconnecting"));
    expect(watch).toHaveBeenCalledTimes(1);
  });

  it("keeps backing off when every connection drops right after its snapshot", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const delays: number[] = [];
    const realSetTimeout = globalThis.setTimeout;
    const spy = vi.spyOn(globalThis, "setTimeout").mockImplementation(((
      fn: () => void,
      ms?: number,
    ) => {
      if (typeof ms === "number" && ms >= 1000) delays.push(ms);
      return realSetTimeout(fn, ms);
    }) as typeof setTimeout);
    const watch: WatchLatest = vi.fn(async function* () {
      yield num(1, "2026-11-08T00:00:00Z", true);
      throw new ConnectError("dropped", Code.Unavailable);
    });
    renderHook(() => useLatestValue("P1", { watch }));

    for (let i = 0; i < 3; i++) {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(10_000);
      });
    }
    spy.mockRestore();

    expect(delays.slice(0, 3)).toEqual([1000, 2000, 4000]);
  });

  it("stops for good when the server refuses", async () => {
    const watch: WatchLatest = vi.fn(async function* () {
      throw new ConnectError("no", Code.PermissionDenied);
    });
    const { result } = renderHook(() => useLatestValue("P1", { watch }));

    await waitFor(() => expect(result.current.status).toBe("unavailable"));
    expect(watch).toHaveBeenCalledTimes(1);
  });

  it("does nothing without a point id, and aborts the stream on unmount", async () => {
    const watch = vi.fn<WatchLatest>((_ids, signal) =>
      controllableStream().iterate(signal),
    );
    const { rerender, unmount } = renderHook(
      ({ id }) => useLatestValue(id, { watch }),
      {
        initialProps: { id: undefined as string | undefined },
      },
    );
    expect(watch).not.toHaveBeenCalled();

    rerender({ id: "P1" });
    await waitFor(() => expect(watch).toHaveBeenCalledTimes(1));
    const signal = watch.mock.calls[0][1];
    unmount();
    expect(signal.aborted).toBe(true);
  });
});
