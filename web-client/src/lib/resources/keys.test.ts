import { describe, expect, it } from "vitest";
import { detailHref, parseRefKey, refKey } from "./keys";

describe("refKey", () => {
  it("keys non-point resources on dtId", () => {
    expect(refKey({ type: "building", dtId: "urn:b1", id: "B1" })).toBe(
      "building:urn:b1",
    );
  });

  it("keys points on their business id", () => {
    expect(refKey({ type: "point", dtId: "urn:pt1", id: "PT001" })).toBe(
      "point:PT001",
    );
  });
});

describe("detailHref", () => {
  it("links non-point resources by dtId under their plural route", () => {
    expect(detailHref({ type: "building", dtId: "urn:b1", id: "B1" })).toBe(
      "/buildings/urn%3Ab1",
    );
    expect(detailHref({ type: "device", dtId: "urn:d1", id: "D1" })).toBe(
      "/devices/urn%3Ad1",
    );
  });

  it("links points by their business id", () => {
    expect(detailHref({ type: "point", dtId: "urn:pt1", id: "PT001" })).toBe(
      "/points/PT001",
    );
  });
});

describe("parseRefKey", () => {
  it("round-trips a non-point key (id may contain colons)", () => {
    expect(parseRefKey("building:urn:b1")).toEqual({
      type: "building",
      id: "urn:b1",
    });
  });

  it("parses a point key", () => {
    expect(parseRefKey("point:PT001")).toEqual({ type: "point", id: "PT001" });
  });

  it("returns null for malformed or unknown-type keys", () => {
    expect(parseRefKey("")).toBeNull();
    expect(parseRefKey("nocolon")).toBeNull();
    expect(parseRefKey(":x")).toBeNull();
    expect(parseRefKey("widget:1")).toBeNull();
    expect(parseRefKey("building:")).toBeNull();
  });
});
