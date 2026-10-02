import { describe, expect, it } from "vitest";
import {
  DEFAULT_EVENTS_QUERY,
  parseEventsQuery,
  serializeEventsQuery,
  summarizeDetail,
  toHealthEvent,
  type HealthEvent,
} from "./events";

type Over = Partial<Omit<HealthEvent, "detail">> & { detail?: Partial<HealthEvent["detail"]> };
const ev = (over: Over): HealthEvent => ({
  id: "e1", subjectType: "point", subjectId: "P1", subjectName: "給気温度", buildingName: "本館",
  kind: "stale", severity: "warn", raisedAt: "2026-10-02T12:00:00Z", clearedAt: null, isOpen: true,
  acknowledgedAt: null, acknowledgedBy: null,
  ...over,
  detail: {
    ageSeconds: null, thresholdSeconds: null, lastSeen: null, reason: null, value: null, violated: null,
    pointCount: null, ...over.detail,
  },
});

describe("events query URL (#455)", () => {
  it("defaults to the open events, any acknowledgement, page 1", () => {
    expect(parseEventsQuery(new URLSearchParams())).toEqual(DEFAULT_EVENTS_QUERY);
    expect(serializeEventsQuery(DEFAULT_EVENTS_QUERY).toString()).toBe("");
  });

  it("keeps lifecycle and ack as two independent filters through a round trip", () => {
    const q = { ...DEFAULT_EVENTS_QUERY, lifecycle: "cleared" as const, ack: "unacked" as const, kinds: ["alarm" as const, "stale" as const], offset: 100 };
    const back = parseEventsQuery(new URLSearchParams(serializeEventsQuery(q).toString()));
    expect(back).toEqual(q);
  });

  it("can ask for both lifecycles explicitly (all), distinct from the open default", () => {
    const q = { ...DEFAULT_EVENTS_QUERY, lifecycle: "" as const };
    const qs = serializeEventsQuery(q).toString();
    expect(qs).toBe("lifecycle=all");
    expect(parseEventsQuery(new URLSearchParams(qs)).lifecycle).toBe("");
  });

  it("falls back to defaults for unknown values and reads comma-separated kinds", () => {
    const q = parseEventsQuery(new URLSearchParams("lifecycle=bogus&ack=maybe&kind=stale,nope,missing&page=zzz"));
    expect(q).toMatchObject({ lifecycle: "open", ack: "", kinds: ["stale", "missing"], offset: 0 });
  });
});

describe("summarizeDetail", () => {
  it("explains a stale event with its age and threshold", () => {
    expect(summarizeDetail(ev({ kind: "stale", detail: { ageSeconds: 900, thresholdSeconds: 300 } }))).toBe(
      "最終受信から 15分（閾値 5分）",
    );
  });
  it("names the missing reason", () => {
    expect(summarizeDetail(ev({ kind: "missing", detail: { reason: "NeverReceived" } }))).toBe("受信履歴なし");
    expect(summarizeDetail(ev({ kind: "missing", detail: { reason: null } }))).toBe("原因不明");
  });
  it("shows the alarm value and the bound it crossed", () => {
    expect(summarizeDetail(ev({ kind: "alarm", detail: { value: 42, violated: "AlarmHigh" } }))).toBe("値 42（AlarmHigh）");
  });
  it("says how many points a gateway outage affects", () => {
    expect(summarizeDetail(ev({ kind: "gateway_offline", subjectType: "gateway", detail: { pointCount: 500 } }))).toBe(
      "配下 500 Point が影響",
    );
  });
});

describe("toHealthEvent", () => {
  it("maps a wire row, keeping lifecycle and acknowledgement apart", () => {
    const e = toHealthEvent({
      id: "x", subjectType: "gateway", subjectId: "GW-1", kind: "gateway_offline", severity: "critical",
      raisedAt: "2026-10-02T12:00:00Z", clearedAt: "2026-10-02T13:00:00Z", isOpen: false,
      acknowledgedAt: "2026-10-02T13:30:00Z", acknowledgedBy: "山田", detail: { pointCount: 3 },
    });
    expect(e).toMatchObject({ isOpen: false, acknowledgedBy: "山田", kind: "gateway_offline" });
    expect(e.detail.pointCount).toBe(3);
  });

  it("falls back for unknown enum values instead of throwing", () => {
    const e = toHealthEvent({ id: "x", kind: "future-kind", severity: "weird" });
    expect(e.kind).toBe("stale");
    expect(e.severity).toBe("warn");
    expect(e.detail.ageSeconds).toBeNull();
  });
});
