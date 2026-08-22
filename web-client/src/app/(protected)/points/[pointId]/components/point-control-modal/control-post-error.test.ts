import { describe, expect, it } from "vitest";
import { controlPostErrorResult } from "./control-post-error";

const axiosError = (status: number, data?: unknown) => ({
  response: { status, data },
});

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

  it("surfaces the server's validation detail on 400 so the operator knows what to fix", () => {
    // The twin's ControlSchema is the source of truth for the allowed range / enum codes, so the
    // server's wording is reused verbatim rather than reconstructed here.
    const result = controlPostErrorResult(
      axiosError(400, {
        error: "value 45 is above the maximum 30",
        dataType: "number",
      }),
      "SOS-PT-006",
    );
    expect(result.status).toBe("failed");
    expect(result.message).toContain("value 45 is above the maximum 30");
  });

  it("does not blame the value for a 400 that is not a schema violation", () => {
    // PointController also returns 400 for "value is required", an unsupported gateway binding, and
    // any dispatch exception (e.g. NATS down). Only the schema violation carries `dataType`; telling
    // the operator to fix their value in the other cases sends them after the wrong thing.
    for (const data of [
      {},
      { error: "value is required" },
      { error: "Connection is closed." },
    ]) {
      const result = controlPostErrorResult(axiosError(400, data), "p1");
      expect(result.status).toBe("failed");
      expect(result.message).toBe("制御信号の送信に失敗しました。");
    }
  });

  it("falls back to a generic failure for other HTTP errors", () => {
    for (const status of [404, 409, 500]) {
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
