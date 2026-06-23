import { describe, expect, it } from "vitest";
import {
  formatKpi,
  serviceDotClass,
  serviceLabel,
  toServiceState,
} from "./format";

describe("toServiceState", () => {
  it("maps known states", () => {
    expect(toServiceState("up")).toBe("up");
    expect(toServiceState("down")).toBe("down");
  });
  it("maps anything else to unknown", () => {
    expect(toServiceState("weird")).toBe("unknown");
    expect(toServiceState("")).toBe("unknown");
  });
});

describe("serviceLabel", () => {
  it("labels each state in Japanese", () => {
    expect(serviceLabel("up")).toBe("稼働");
    expect(serviceLabel("down")).toBe("停止");
    expect(serviceLabel("nope")).toBe("不明");
  });
});

describe("serviceDotClass", () => {
  it("colors up green, down red, unknown gray", () => {
    expect(serviceDotClass("up")).toContain("green");
    expect(serviceDotClass("down")).toContain("red");
    expect(serviceDotClass("x")).toContain("gray");
  });
});

describe("formatKpi", () => {
  it("renders an em dash for null/undefined/NaN (metrics unavailable)", () => {
    expect(formatKpi(null)).toBe("—");
    expect(formatKpi(undefined)).toBe("—");
    expect(formatKpi(Number.NaN)).toBe("—");
  });
  it("formats numbers and keeps zero as a real value", () => {
    expect(formatKpi(0)).toBe("0");
    expect(formatKpi(1240)).toBe("1,240");
  });
  it("applies a suffix", () => {
    expect(formatKpi(2, { suffix: " 件/分" })).toBe("2 件/分");
  });
});
