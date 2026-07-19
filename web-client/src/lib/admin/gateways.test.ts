import { describe, expect, it } from "vitest";
import {
  bindingLabel,
  connectedLabel,
  lastSeenLabel,
  pointlistSyncedLabel,
  pointlistSyncedTone,
  shortRevision,
} from "./gateways";

describe("gateways display helpers", () => {
  it("connectedLabel reflects the live egress connection state (#230)", () => {
    expect(connectedLabel(true)).toBe("接続中");
    expect(connectedLabel(false)).toBe("未接続");
  });

  it("pointlistSyncedLabel/Tone reflect the tri-state sync signal (#230 Phase 2b)", () => {
    expect(pointlistSyncedLabel(true)).toBe("同期済み");
    expect(pointlistSyncedLabel(false)).toBe("未同期");
    expect(pointlistSyncedLabel(null)).toBe("同期状態不明");
    expect(pointlistSyncedTone(true)).toBe("ok");
    expect(pointlistSyncedTone(false)).toBe("warn");
    expect(pointlistSyncedTone(null)).toBe("unknown");
  });

  it("labels known bindings, passes through unknown", () => {
    expect(bindingLabel("hono")).toBe("Hono (AMQP)");
    expect(bindingLabel("kandt")).toBe("Kandt (IoT Hub)");
    expect(bindingLabel("bacnet-sim")).toBe("BACnet Sim");
    expect(bindingLabel("custom")).toBe("custom");
  });

  it("shortens sha256 revision", () => {
    expect(shortRevision("sha256:0123456789abcdef0000")).toBe("0123456789ab");
    expect(shortRevision("")).toBe("—");
    expect(shortRevision("abcdef")).toBe("abcdef");
  });

  describe("lastSeenLabel (#181 Phase 2)", () => {
    const now = new Date("2026-07-18T12:00:00Z");

    it("shows 受信なし for null/invalid", () => {
      expect(lastSeenLabel(null, now)).toBe("受信なし");
      expect(lastSeenLabel(undefined, now)).toBe("受信なし");
      expect(lastSeenLabel("not-a-date", now)).toBe("受信なし");
    });

    it("renders a coarse relative age", () => {
      expect(lastSeenLabel("2026-07-18T11:59:30Z", now)).toBe("30秒前");
      expect(lastSeenLabel("2026-07-18T11:45:00Z", now)).toBe("15分前");
      expect(lastSeenLabel("2026-07-18T09:00:00Z", now)).toBe("3時間前");
      expect(lastSeenLabel("2026-07-16T12:00:00Z", now)).toBe("2日前");
    });

    it("clamps a future timestamp to 0秒前", () => {
      expect(lastSeenLabel("2026-07-18T12:00:30Z", now)).toBe("0秒前");
    });
  });
});
