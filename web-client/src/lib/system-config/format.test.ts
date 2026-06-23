import { describe, expect, it } from "vitest";
import { configValueDisplay, isUnset } from "./format";

describe("configValueDisplay", () => {
  it("shows a non-secret value as-is", () => {
    expect(
      configValueDisplay({ key: "NATS_URL", isSecret: false, isSet: true, value: "nats://x:4222" }),
    ).toBe("nats://x:4222");
  });

  it("shows 未設定 for an unset non-secret", () => {
    expect(configValueDisplay({ key: "PROMETHEUS_URL", isSecret: false, isSet: false })).toBe("未設定");
  });

  it("shows presence-only for a set secret (never the value)", () => {
    const out = configValueDisplay({
      key: "POSTGRES_CONNECTION_STRING",
      isSecret: true,
      isSet: true,
      value: null,
    });
    expect(out).toBe("設定済み");
  });

  it("shows 未設定 for an unset secret", () => {
    expect(
      configValueDisplay({ key: "KEYCLOAK_ADMIN_CLIENT_SECRET", isSecret: true, isSet: false }),
    ).toBe("未設定");
  });

  it("never leaks a secret value even if one is mistakenly present", () => {
    const out = configValueDisplay({ key: "X", isSecret: true, isSet: true, value: "hunter2" });
    expect(out).not.toContain("hunter2");
    expect(out).toBe("設定済み");
  });
});

describe("isUnset", () => {
  it("is true only when not set", () => {
    expect(isUnset({ key: "A", isSecret: false, isSet: false })).toBe(true);
    expect(isUnset({ key: "B", isSecret: false, isSet: true, value: "v" })).toBe(false);
  });
});
