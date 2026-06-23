import { describe, expect, it } from "vitest";
import { bindingLabel, shortRevision } from "./gateways";

describe("gateways display helpers", () => {
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
});
