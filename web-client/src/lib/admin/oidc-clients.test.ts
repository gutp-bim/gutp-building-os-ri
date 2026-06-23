import { describe, expect, it } from "vitest";
import { clientStatusLabel, clientStatusBadgeClass, clientTypeLabel } from "./oidc-clients";

describe("oidc-clients display helpers", () => {
  it("labels status", () => {
    expect(clientStatusLabel({ enabled: true })).toBe("有効");
    expect(clientStatusLabel({ enabled: false })).toBe("無効");
  });

  it("colors status badge by enabled", () => {
    expect(clientStatusBadgeClass({ enabled: true })).toContain("green");
    expect(clientStatusBadgeClass({ enabled: false })).toContain("gray");
  });

  it("labels client type by service-account flag", () => {
    expect(clientTypeLabel({ serviceAccountsEnabled: true })).toBe("サービスアカウント");
    expect(clientTypeLabel({ serviceAccountsEnabled: false })).toBe("標準クライアント");
  });
});
