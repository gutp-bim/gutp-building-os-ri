import { describe, expect, it } from "vitest";
import { canApplyImport, previewSummary, type TwinImportPreview } from "./twin-admin";

const valid: TwinImportPreview = { tripleCount: 100, gatewayCount: 3, collisions: [], valid: true };
const invalid: TwinImportPreview = {
  tripleCount: 100,
  gatewayCount: 2,
  collisions: [{ gatewayId: "GW001", buildingCount: 2 }],
  valid: false,
};

describe("canApplyImport", () => {
  it("allows a valid preview with no collisions", () => {
    expect(canApplyImport(valid)).toBe(true);
  });
  it("blocks when collisions exist", () => {
    expect(canApplyImport(invalid)).toBe(false);
  });
  it("blocks when no preview yet", () => {
    expect(canApplyImport(null)).toBe(false);
  });
});

describe("previewSummary", () => {
  it("summarizes a valid preview", () => {
    expect(previewSummary(valid)).toContain("検証 OK");
    expect(previewSummary(valid)).toContain("100 トリプル");
  });
  it("flags collisions", () => {
    expect(previewSummary(invalid)).toContain("gateway_id 重複 1 件");
  });
});
