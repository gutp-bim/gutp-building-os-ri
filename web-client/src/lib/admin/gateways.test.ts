import { describe, expect, it } from "vitest";
import {
  bindingLabel,
  connectedLabel,
  lastSeenLabel,
  shortRevision,
} from "./gateways";

describe("gateways display helpers", () => {
  it("connectedLabel reflects the live egress connection state (#230)", () => {
    expect(connectedLabel(true)).toBe("接続中");
    expect(connectedLabel(false)).toBe("未接続");
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
