import { describe, expect, it } from "vitest";
import { resolveUnitLabel } from "./telemetry-helper";

describe("resolveUnitLabel", () => {
  it("QUDT IRI を表示ラベルに解決する", () => {
    expect(resolveUnitLabel("http://qudt.org/vocab/unit/DEG_C")).toBe("°C");
  });

  it("短縮コード（CSV 由来の twin が持ち込む表記）も解決する", () => {
    expect(resolveUnitLabel("degC")).toBe("°C");
    expect(resolveUnitLabel("percent")).toBe("%");
  });

  it("未知の単位は生値のまま返す（表示を空にしない）", () => {
    expect(resolveUnitLabel("ppm")).toBe("ppm");
  });

  it("未設定は null（呼び出し側は単位を付けない）", () => {
    expect(resolveUnitLabel(null)).toBeNull();
    expect(resolveUnitLabel(undefined)).toBeNull();
    expect(resolveUnitLabel("")).toBeNull();
  });
});
