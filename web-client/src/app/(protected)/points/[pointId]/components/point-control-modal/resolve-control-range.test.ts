import type { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { describe, expect, it } from "vitest";

import {
  controlRangeLabel,
  initialControlValue,
  resolveControlRange,
} from "./resolve-control-range";

const detail = (
  point: Record<string, unknown>,
  controlSchema?: Record<string, unknown>,
) => ({ point, controlSchema }) as unknown as PointDetail;

describe("resolveControlRange (#298)", () => {
  it("prefers the ControlSchema bounds, which are what the server validates against", () => {
    const range = resolveControlRange(
      detail(
        { minPresValue: -10, maxPresValue: 50 },
        { dataType: "number", minValue: 16, maxValue: 30 },
      ),
    );

    expect(range).toEqual({ min: 16, max: 30 });
  });

  it("falls back to the BACnet raw span when the point has no ControlSchema", () => {
    // twin (sbco:minPresValue / maxPresValue) が持っている値。配線前はここが常に
    // undefined で、モーダルは 0〜100 に落ちていた。
    expect(
      resolveControlRange(detail({ minPresValue: -10, maxPresValue: 50 })),
    ).toEqual({
      min: -10,
      max: 50,
    });
  });

  it("falls back per bound, so a half-specified ControlSchema keeps the other side", () => {
    expect(
      resolveControlRange(
        detail(
          { minPresValue: -10, maxPresValue: 50 },
          { dataType: "number", maxValue: 30 },
        ),
      ),
    ).toEqual({ min: -10, max: 30 });
  });

  it("keeps a zero bound instead of treating it as absent", () => {
    expect(
      resolveControlRange(detail({ minPresValue: 0, maxPresValue: 0 })),
    ).toEqual({
      min: 0,
      max: 0,
    });
  });

  it("reports an unknown range rather than inventing 0〜100", () => {
    expect(resolveControlRange(detail({}))).toEqual({
      min: undefined,
      max: undefined,
    });
    expect(
      resolveControlRange(detail({ minPresValue: null, maxPresValue: null })),
    ).toEqual({ min: undefined, max: undefined });
  });
});

describe("initialControlValue", () => {
  it("starts at the lower bound when there is one", () => {
    expect(initialControlValue({ min: 16, max: 30 })).toBe(16);
    expect(initialControlValue({ min: -10, max: 50 })).toBe(-10);
  });

  it("does not start outside a known upper bound when no lower bound exists", () => {
    // 上限だけが負の点。素朴な `min ?? 0` だと初期値 0 がいきなり範囲外になる。
    expect(initialControlValue({ max: -5 })).toBe(-5);
  });

  it("keeps 0 when 0 is inside the known bounds", () => {
    expect(initialControlValue({ max: 100 })).toBe(0);
    expect(initialControlValue({})).toBe(0);
  });

  it("respects a zero lower bound", () => {
    expect(initialControlValue({ min: 0, max: 100 })).toBe(0);
  });
});

describe("controlRangeLabel", () => {
  it("labels a full range", () => {
    expect(controlRangeLabel({ min: 16, max: 30 })).toBe("16～30");
  });

  it("labels a one-sided range without implying the missing bound", () => {
    expect(controlRangeLabel({ min: 16 })).toBe("16 以上");
    expect(controlRangeLabel({ max: 30 })).toBe("30 以下");
  });

  it("returns null when neither bound is known", () => {
    expect(controlRangeLabel({})).toBeNull();
  });

  it("labels a zero bound", () => {
    expect(controlRangeLabel({ min: 0, max: 0 })).toBe("0～0");
  });
});
