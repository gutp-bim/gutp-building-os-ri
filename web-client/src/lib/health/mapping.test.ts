import { describe, expect, it } from "vitest";
import {
  alarmBoundLabel,
  alarmLabel,
  explainRowThreshold,
  formatAge,
  freshnessLabel,
  healthStatusLabel,
  missingReasonLabel,
  thresholdSourceLabel,
  toHealthRow,
  type HealthRow,
} from "./mapping";

/** サーバが返す 1 行（enum は PascalCase）。 */
const wire = {
  pointId: "PT001",
  name: "給気温度",
  unit: "degC",
  freshnessStatus: "Stale",
  alarmStatus: "Suppressed",
  healthStatus: "Stale",
  lastSeen: "2026-09-10T10:23:42Z",
  ageSeconds: 1080,
  expectedIntervalSeconds: 300,
  thresholdSeconds: 900,
  thresholdSource: "Point",
  missingReason: null,
  value: 23.4,
  violated: null,
  gatewayId: "GW-001",
  gatewayConnected: true,
  deviceName: "AHU-01",
  spaceName: "会議室A",
  floorName: "3F",
  buildingName: "本館",
  tags: ["hvac", "critical"],
};

describe("toHealthRow", () => {
  it("完全な行をそのままドメイン型に写し、enum を小文字 union に正規化する", () => {
    expect(toHealthRow(wire)).toEqual<HealthRow>({
      pointId: "PT001",
      name: "給気温度",
      unit: "degC",
      freshnessStatus: "stale",
      alarmStatus: "suppressed",
      healthStatus: "stale",
      lastSeen: "2026-09-10T10:23:42Z",
      ageSeconds: 1080,
      expectedIntervalSeconds: 300,
      thresholdSeconds: 900,
      thresholdSource: "point",
      missingReason: null,
      value: 23.4,
      violated: null,
      gatewayId: "GW-001",
      gatewayConnected: true,
      deviceName: "AHU-01",
      spaceName: "会議室A",
      floorName: "3F",
      buildingName: "本館",
      tags: ["hvac", "critical"],
    });
  });

  it("PascalCase の missingReason / violated も正規化する", () => {
    const row = toHealthRow({
      ...wire,
      freshnessStatus: "Missing",
      missingReason: "GatewayDisconnected",
      alarmStatus: "Critical",
      violated: "AlarmHigh",
    });

    expect(row.missingReason).toBe("gatewayDisconnected");
    expect(row.violated).toBe("alarmHigh");
  });

  it("未知の状態値は unknown に倒す（新しい状態値が増えても画面を壊さない）", () => {
    const row = toHealthRow({
      ...wire,
      freshnessStatus: "Degraded",
      alarmStatus: "Degraded",
      healthStatus: "Degraded",
    });

    expect(row.freshnessStatus).toBe("unknown");
    expect(row.alarmStatus).toBe("unknown");
    expect(row.healthStatus).toBe("unknown");
  });

  it("未知の thresholdSource は system、未知の violated は null に倒す", () => {
    const row = toHealthRow({
      ...wire,
      thresholdSource: "Tenant",
      violated: "AlarmMiddle",
    });

    expect(row.thresholdSource).toBe("system");
    expect(row.violated).toBeNull();
  });

  it("欠測でない行の missingReason は落とす（理由が付いて見えると誤読される）", () => {
    const row = toHealthRow({
      ...wire,
      freshnessStatus: "Fresh",
      missingReason: "NeverReceived",
    });

    expect(row.missingReason).toBeNull();
  });

  it("欠測行の未知の理由は unknown（理由不明）として残す", () => {
    const row = toHealthRow({
      ...wire,
      freshnessStatus: "Missing",
      missingReason: "SomethingNew",
    });

    expect(row.missingReason).toBe("unknown");
  });

  it("フィールドが丸ごと欠けていても既定値で埋める", () => {
    expect(toHealthRow({ pointId: "PT002" })).toEqual<HealthRow>({
      pointId: "PT002",
      name: "PT002",
      freshnessStatus: "unknown",
      alarmStatus: "unknown",
      healthStatus: "unknown",
      lastSeen: null,
      ageSeconds: null,
      expectedIntervalSeconds: null,
      thresholdSeconds: 300,
      thresholdSource: "system",
      missingReason: null,
      value: null,
      violated: null,
      gatewayId: null,
      gatewayConnected: null,
      tags: [],
    });
  });

  it("name が空なら pointId で代替する（名前欄が空の行を出さない）", () => {
    expect(toHealthRow({ ...wire, name: "   " }).name).toBe("PT001");
    expect(toHealthRow({ ...wire, name: 42 }).name).toBe("PT001");
  });

  it("行そのものが object でなくても既定行を返す", () => {
    for (const broken of [null, undefined, "PT001", 7, []]) {
      const row = toHealthRow(broken);
      expect(row.pointId).toBe("");
      expect(row.healthStatus).toBe("unknown");
      expect(row.tags).toEqual([]);
    }
  });

  it("型が壊れた数値フィールドは null に倒す", () => {
    const row = toHealthRow({
      ...wire,
      ageSeconds: "1080",
      value: "23.4",
      expectedIntervalSeconds: 0,
      thresholdSeconds: -1,
    });

    expect(row.ageSeconds).toBeNull();
    expect(row.value).toBeNull();
    // 0 秒周期は「常に欠測」になるので採用しない。閾値も正の値でなければ既定に戻す。
    expect(row.expectedIntervalSeconds).toBeNull();
    expect(row.thresholdSeconds).toBe(300);
  });

  it("ageSeconds は整数秒に floor し、負値は 0 でクリップする", () => {
    expect(toHealthRow({ ...wire, ageSeconds: 42.8 }).ageSeconds).toBe(42);
    expect(toHealthRow({ ...wire, ageSeconds: -30 }).ageSeconds).toBe(0);
  });

  it("lastSeen が文字列でなければ null", () => {
    expect(toHealthRow({ ...wire, lastSeen: 1757500000 }).lastSeen).toBeNull();
    expect(toHealthRow({ ...wire, lastSeen: "" }).lastSeen).toBeNull();
  });

  it("gatewayConnected は boolean のときだけ採用し、他は null（未知と切断を混同しない）", () => {
    expect(
      toHealthRow({ ...wire, gatewayConnected: false }).gatewayConnected,
    ).toBe(false);
    expect(
      toHealthRow({ ...wire, gatewayConnected: "true" }).gatewayConnected,
    ).toBe(null);
    expect(toHealthRow({ ...wire, gatewayId: "" }).gatewayId).toBeNull();
  });

  it("任意の名称フィールドは空・非文字列なら省く", () => {
    const row = toHealthRow({
      ...wire,
      unit: "",
      deviceName: null,
      spaceName: "  ",
      floorName: 3,
      buildingName: "本館",
    });

    expect(row.unit).toBeUndefined();
    expect(row.deviceName).toBeUndefined();
    expect(row.spaceName).toBeUndefined();
    expect(row.floorName).toBeUndefined();
    expect(row.buildingName).toBe("本館");
  });

  it("tags は文字列要素のみ・trim・重複除去、配列でなければ空配列", () => {
    expect(
      toHealthRow({ ...wire, tags: [" hvac ", "hvac", "", 1] }).tags,
    ).toEqual(["hvac"]);
    expect(toHealthRow({ ...wire, tags: "hvac" }).tags).toEqual([]);
  });
});

describe("formatAge", () => {
  it("秒 → 分 → 時間 → 日 の 1 単位に丸める", () => {
    expect(formatAge(0)).toBe("0秒前");
    expect(formatAge(59)).toBe("59秒前");
    expect(formatAge(18 * 60)).toBe("18分前");
    expect(formatAge(2 * 3600)).toBe("2時間前");
    expect(formatAge(3 * 86400)).toBe("3日前");
  });

  it("未受信（null）は「—」", () => {
    expect(formatAge(null)).toBe("—");
  });
});

describe("ラベル", () => {
  it("鮮度", () => {
    expect(freshnessLabel("fresh")).toBe("最新");
    expect(freshnessLabel("stale")).toBe("鮮度切れ");
    expect(freshnessLabel("missing")).toBe("欠測");
    expect(freshnessLabel("unknown")).toBe("判定不能");
  });

  it("値アラーム（抑止と未評価を「正常」と描かない）", () => {
    expect(alarmLabel("normal")).toBe("正常");
    expect(alarmLabel("warn")).toBe("注意");
    expect(alarmLabel("critical")).toBe("異常");
    expect(alarmLabel("suppressed")).toBe("評価対象外");
    expect(alarmLabel("unknown")).toBe("未評価");
  });

  it("総合ステータス", () => {
    expect(healthStatusLabel("critical")).toBe("異常");
    expect(healthStatusLabel("warn")).toBe("注意");
    expect(healthStatusLabel("missing")).toBe("欠測");
    expect(healthStatusLabel("stale")).toBe("鮮度切れ");
    expect(healthStatusLabel("unknown")).toBe("判定不能");
    expect(healthStatusLabel("fresh")).toBe("正常");
  });

  it("欠測の理由", () => {
    expect(missingReasonLabel("neverReceived")).toBe("受信履歴なし");
    expect(missingReasonLabel("gatewayDisconnected")).toBe("ゲートウェイ切断");
    expect(missingReasonLabel("unknown")).toBe("原因不明");
    expect(missingReasonLabel(null)).toBe("—");
  });

  it("閾値の由来", () => {
    expect(thresholdSourceLabel("point")).toBe("Point 個別");
    expect(thresholdSourceLabel("device")).toBe("機器");
    expect(thresholdSourceLabel("gateway")).toBe("ゲートウェイ");
    expect(thresholdSourceLabel("system")).toBe("システム既定");
  });

  it("破られた閾値", () => {
    expect(alarmBoundLabel("alarmHigh")).toBe("異常上限");
    expect(alarmBoundLabel("alarmLow")).toBe("異常下限");
    expect(alarmBoundLabel("warnHigh")).toBe("注意上限");
    expect(alarmBoundLabel("warnLow")).toBe("注意下限");
    expect(alarmBoundLabel(null)).toBe("—");
  });
});

describe("explainRowThreshold", () => {
  it("期待周期があれば「期待周期 × 倍率 = 閾値」で説明する", () => {
    expect(explainRowThreshold(toHealthRow(wire))).toBe(
      "期待周期 5分 × 3 = 15分",
    );
  });

  it("期待周期が無ければ既定閾値であることと倍率が効かない理由を述べる", () => {
    const row = toHealthRow({
      ...wire,
      expectedIntervalSeconds: null,
      thresholdSeconds: 300,
      thresholdSource: "System",
    });

    expect(explainRowThreshold(row)).toBe(
      "既定閾値 5分（期待周期が未設定のため倍率は適用されません）",
    );
  });

  it("割り切れない倍率も閾値と一致した文言になる", () => {
    const row = toHealthRow({
      ...wire,
      expectedIntervalSeconds: 300,
      thresholdSeconds: 1000,
    });

    expect(explainRowThreshold(row)).toBe("期待周期 5分 × 3.3 = 16.7分");
  });
});
