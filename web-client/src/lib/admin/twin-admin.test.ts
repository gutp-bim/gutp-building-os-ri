import { describe, expect, it } from "vitest";
import {
  canApplyImport,
  controlSchemaIssueReasonLabel,
  orphanReasonLabel,
  previewSummary,
  type TwinImportPreview,
} from "./twin-admin";

const valid: TwinImportPreview = {
  tripleCount: 100,
  gatewayCount: 3,
  collisions: [],
  orphanCount: 0,
  orphans: [],
  controlSchemaIssueCount: 0,
  controlSchemaIssues: [],
  valid: true,
};
const invalid: TwinImportPreview = {
  tripleCount: 100,
  gatewayCount: 2,
  collisions: [{ gatewayId: "GW001", buildingCount: 2 }],
  orphanCount: 0,
  orphans: [],
  controlSchemaIssueCount: 0,
  controlSchemaIssues: [],
  valid: false,
};
const orphaned: TwinImportPreview = {
  tripleCount: 100,
  gatewayCount: 1,
  collisions: [],
  orphanCount: 2,
  orphans: [
    { resourceId: "urn:pt:1", reason: "no_device" },
    { resourceId: "urn:pt:2", reason: "no_building_path" },
  ],
  controlSchemaIssueCount: 0,
  controlSchemaIssues: [],
  valid: false,
};
const schemaIssues: TwinImportPreview = {
  tripleCount: 100,
  gatewayCount: 1,
  collisions: [],
  orphanCount: 0,
  orphans: [],
  controlSchemaIssueCount: 2,
  controlSchemaIssues: [
    { pointId: "urn:pt:1", reason: "missing_datatype" },
    { pointId: "urn:pt:2", reason: "malformed_enum_labels" },
  ],
  valid: true,
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
  it("blocks orphans unless overridden", () => {
    expect(canApplyImport(orphaned)).toBe(false);
    expect(canApplyImport(orphaned, true)).toBe(true);
  });
  it("never lets the orphan override waive a collision", () => {
    expect(canApplyImport({ ...invalid, orphanCount: 1 }, true)).toBe(false);
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
  it("flags orphans", () => {
    expect(previewSummary(orphaned)).toContain("階層未接続 2 件");
    expect(previewSummary(orphaned)).not.toContain("検証 OK");
  });
  it("flags both at once", () => {
    const summary = previewSummary({ ...invalid, orphanCount: 3 });
    expect(summary).toContain("gateway_id 重複 1 件");
    expect(summary).toContain("階層未接続 3 件");
  });
  it("flags control-schema issues without affecting validity", () => {
    expect(previewSummary(schemaIssues)).toContain("制御スキーマ不整合 2 件");
    // #336 is observation-only — a schema issue never marks the preview invalid.
    expect(previewSummary(schemaIssues)).not.toContain("検証 OK");
    expect(schemaIssues.valid).toBe(true);
  });
});

describe("orphanReasonLabel", () => {
  it("labels the three missing links", () => {
    expect(orphanReasonLabel("no_device")).toBe("デバイス未接続");
    expect(orphanReasonLabel("no_room")).toBe("空間未接続（部屋・フロア未指定）");
    expect(orphanReasonLabel("no_building_path")).toBe("フロア・建物へ到達不能");
  });
  it("passes an unknown reason through", () => {
    expect(orphanReasonLabel("no_such_reason")).toBe("no_such_reason");
  });
});

describe("controlSchemaIssueReasonLabel", () => {
  it("labels the two schema-issue reasons", () => {
    expect(controlSchemaIssueReasonLabel("missing_datatype")).toBe(
      "制御スキーマ未設定（dataType 欠落）",
    );
    expect(controlSchemaIssueReasonLabel("malformed_enum_labels")).toBe(
      "enumLabels が不正な JSON",
    );
  });
  it("passes an unknown reason through", () => {
    expect(controlSchemaIssueReasonLabel("no_such_reason")).toBe("no_such_reason");
  });
});
