import { describe, expect, it } from "vitest";
import { controlPostErrorResult } from "./control-post-error";

const axiosError = (status: number) => ({ response: { status } });

describe("controlPostErrorResult", () => {
  it("maps 403 to a permission-denied explanation naming the required permission", () => {
    const result = controlPostErrorResult(axiosError(403), "SOS-PT-001");
    expect(result.status).toBe("permission_denied");
    expect(result.message).toContain("point:SOS-PT-001:write");
    expect(result.message).toContain("権限が必要");
  });

  it("maps 503 to a gateway-offline explanation", () => {
    const result = controlPostErrorResult(axiosError(503), "SOS-PT-001");
    expect(result.status).toBe("gateway_offline");
    expect(result.message).toContain("ゲートウェイ");
  });

  it("falls back to a generic failure for other HTTP errors", () => {
    for (const status of [400, 404, 409, 500]) {
      const result = controlPostErrorResult(axiosError(status), "p1");
      expect(result.status).toBe("failed");
    }
  });

  it("falls back to a generic failure when there is no response (network error)", () => {
    expect(controlPostErrorResult(new Error("boom"), "p1").status).toBe(
      "failed",
    );
    expect(controlPostErrorResult(undefined, "p1").status).toBe("failed");
    expect(
      controlPostErrorResult({ response: { status: "oops" } }, "p1").status,
    ).toBe("failed");
  });
});
