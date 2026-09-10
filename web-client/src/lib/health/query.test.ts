import { describe, expect, it } from "vitest";
import {
  DEFAULT_HEALTH_QUERY,
  normalizeHealthQuery,
  parseDuration,
  parseHealthQuery,
  serializeHealthQuery,
  type HealthQuery,
} from "./query";

const parse = (search: string) => parseHealthQuery(new URLSearchParams(search));
const serialize = (q: HealthQuery) => serializeHealthQuery(q).toString();

describe("parseHealthQuery", () => {
  it("空のクエリは既定値になる", () => {
    expect(parse("")).toEqual(DEFAULT_HEALTH_QUERY);
    expect(DEFAULT_HEALTH_QUERY).toEqual({
      freshness: [],
      alarm: [],
      healthStatus: [],
      tags: [],
      sort: "worst",
      limit: 100,
      offset: 0,
    });
  });

  it("繰り返しパラメータを受け付ける", () => {
    expect(parse("freshness=stale&freshness=missing").freshness).toEqual([
      "stale",
      "missing",
    ]);
  });

  it("カンマ区切りを受け付ける", () => {
    expect(parse("freshness=stale,missing").freshness).toEqual([
      "stale",
      "missing",
    ]);
    expect(parse("healthStatus=critical,warn").healthStatus).toEqual([
      "critical",
      "warn",
    ]);
    expect(parse("alarm=warn,critical").alarm).toEqual(["warn", "critical"]);
  });

  it("繰り返しとカンマ区切りの混在を受け付け、重複は順序を保って除去する", () => {
    expect(
      parse("freshness=stale,missing&freshness=stale,fresh").freshness,
    ).toEqual(["stale", "missing", "fresh"]);
  });

  it("未知の値と空文字は捨てる", () => {
    expect(parse("freshness=stale,bogus,,fresh").freshness).toEqual([
      "stale",
      "fresh",
    ]);
    expect(parse("freshness=bogus").freshness).toEqual([]);
    expect(parse("alarm=ok").alarm).toEqual([]);
  });

  it("大文字・PascalCase の値も受け付ける（API 契約の表記ゆれ吸収）", () => {
    expect(parse("freshness=Stale&healthStatus=Critical").freshness).toEqual([
      "stale",
    ]);
    expect(parse("healthStatus=Critical").healthStatus).toEqual(["critical"]);
  });

  it("スカラーのフィルタを読み取る", () => {
    const q = parse(
      "buildingDtId=b1&floorDtId=f1&deviceDtId=d1&gatewayId=GW-001&q=%20SAT%20",
    );
    expect(q.buildingDtId).toBe("b1");
    expect(q.floorDtId).toBe("f1");
    expect(q.deviceDtId).toBe("d1");
    expect(q.gatewayId).toBe("GW-001");
    expect(q.q).toBe("SAT");
  });

  it("空文字のスカラーは未指定として扱う", () => {
    const q = parse("buildingDtId=&q=%20%20&gatewayId=");
    expect(q.buildingDtId).toBeUndefined();
    expect(q.q).toBeUndefined();
    expect(q.gatewayId).toBeUndefined();
  });

  it("tag は繰り返し指定・重複除去・順序保持", () => {
    expect(parse("tag=critical&tag=hvac&tag=critical").tags).toEqual([
      "critical",
      "hvac",
    ]);
    expect(parse("tag=%20&tag=hvac").tags).toEqual(["hvac"]);
  });

  it("olderThan は秒数・期間表記の両方を受け付ける", () => {
    expect(parse("olderThan=1800").olderThanSeconds).toBe(1800);
    expect(parse("olderThan=30m").olderThanSeconds).toBe(1800);
    expect(parse("olderThan=bogus").olderThanSeconds).toBeUndefined();
    expect(parse("olderThan=").olderThanSeconds).toBeUndefined();
    expect(parse("olderThan=-5").olderThanSeconds).toBeUndefined();
  });

  it("sort は既知の値のみ、未知なら worst に倒す", () => {
    expect(parse("sort=lastSeen").sort).toBe("lastSeen");
    expect(parse("sort=name").sort).toBe("name");
    expect(parse("sort=bogus").sort).toBe("worst");
  });

  it("limit は 1..500 に clamp し、数値でなければ既定 100", () => {
    expect(parse("limit=50").limit).toBe(50);
    expect(parse("limit=0").limit).toBe(1);
    expect(parse("limit=-10").limit).toBe(1);
    expect(parse("limit=999").limit).toBe(500);
    expect(parse("limit=abc").limit).toBe(100);
    expect(parse("limit=25.7").limit).toBe(25);
  });

  it("offset は負なら 0、数値でなければ 0", () => {
    expect(parse("offset=200").offset).toBe(200);
    expect(parse("offset=-1").offset).toBe(0);
    expect(parse("offset=abc").offset).toBe(0);
  });
});

describe("serializeHealthQuery", () => {
  it("既定値のフィールドは出力しない（URL を短く保つ）", () => {
    expect(serialize(DEFAULT_HEALTH_QUERY)).toBe("");
    expect(serialize({ ...DEFAULT_HEALTH_QUERY, limit: 100, offset: 0 })).toBe(
      "",
    );
  });

  it("複数値はカンマ区切りで 1 パラメータにまとめる", () => {
    const out = serializeHealthQuery({
      ...DEFAULT_HEALTH_QUERY,
      freshness: ["stale", "missing"],
      alarm: ["critical"],
    });
    expect(out.get("freshness")).toBe("stale,missing");
    expect(out.get("alarm")).toBe("critical");
    expect(out.getAll("freshness")).toHaveLength(1);
  });

  it("tag は繰り返しパラメータで出す（カンマを含むタグを壊さない）", () => {
    const out = serializeHealthQuery({
      ...DEFAULT_HEALTH_QUERY,
      tags: ["critical", "hvac"],
    });
    expect(out.getAll("tag")).toEqual(["critical", "hvac"]);
  });

  it("olderThan は秒数で出す", () => {
    const out = serializeHealthQuery({
      ...DEFAULT_HEALTH_QUERY,
      olderThanSeconds: 1800,
    });
    expect(out.get("olderThan")).toBe("1800");
  });

  it("既定でない limit / offset / sort は出力する", () => {
    const out = serializeHealthQuery({
      ...DEFAULT_HEALTH_QUERY,
      sort: "name",
      limit: 50,
      offset: 100,
    });
    expect(out.get("sort")).toBe("name");
    expect(out.get("limit")).toBe("50");
    expect(out.get("offset")).toBe("100");
  });

  it("入力を正規化してから出力する（clamp・重複除去・trim）", () => {
    const out = serializeHealthQuery({
      ...DEFAULT_HEALTH_QUERY,
      tags: ["hvac", " hvac ", ""],
      limit: 9999,
      offset: -5,
      q: "  SAT  ",
    });
    expect(out.getAll("tag")).toEqual(["hvac"]);
    expect(out.get("limit")).toBe("500");
    expect(out.get("offset")).toBeNull();
    expect(out.get("q")).toBe("SAT");
  });
});

describe("round-trip", () => {
  it("parse(serialize(q)) が正規化済み q と等しい", () => {
    const q: HealthQuery = {
      freshness: ["stale", "missing"],
      alarm: ["critical", "warn"],
      healthStatus: ["critical"],
      olderThanSeconds: 900,
      buildingDtId: "b1",
      floorDtId: "f1",
      deviceDtId: "d1",
      gatewayId: "GW-001",
      tags: ["critical", "hvac"],
      q: "SAT",
      sort: "lastSeen",
      limit: 200,
      offset: 400,
    };
    expect(parseHealthQuery(serializeHealthQuery(q))).toEqual(
      normalizeHealthQuery(q),
    );
  });

  it("既定値のみの q も round-trip する", () => {
    expect(
      parseHealthQuery(serializeHealthQuery(DEFAULT_HEALTH_QUERY)),
    ).toEqual(DEFAULT_HEALTH_QUERY);
  });

  it("正規化が必要な q でも round-trip する", () => {
    const q: HealthQuery = {
      ...DEFAULT_HEALTH_QUERY,
      tags: ["hvac", "hvac", " "],
      limit: 0,
      offset: -3,
      q: "  室温 ",
    };
    expect(parseHealthQuery(serializeHealthQuery(q))).toEqual(
      normalizeHealthQuery(q),
    );
  });
});

describe("parseDuration", () => {
  it("単位付き・単位なしの両方を秒に変換する", () => {
    expect(parseDuration("45s")).toBe(45);
    expect(parseDuration("30m")).toBe(1800);
    expect(parseDuration("1h")).toBe(3600);
    expect(parseDuration("2d")).toBe(172800);
    expect(parseDuration("1800")).toBe(1800);
  });

  it("大文字・前後の空白を許容する", () => {
    expect(parseDuration(" 30M ")).toBe(1800);
    expect(parseDuration("1H")).toBe(3600);
  });

  it("小数は秒に floor する", () => {
    expect(parseDuration("1.5m")).toBe(90);
    expect(parseDuration("0.5s")).toBe(0);
  });

  it("不正な入力は null", () => {
    expect(parseDuration("")).toBeNull();
    expect(parseDuration("   ")).toBeNull();
    expect(parseDuration("abc")).toBeNull();
    expect(parseDuration("30x")).toBeNull();
    expect(parseDuration("-30m")).toBeNull();
    expect(parseDuration("m30")).toBeNull();
    expect(parseDuration("Infinity")).toBeNull();
  });
});
