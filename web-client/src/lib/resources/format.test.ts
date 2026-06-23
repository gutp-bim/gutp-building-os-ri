import { describe, expect, it } from "vitest";
import { decodeDtIdForDisplay } from "./format";

describe("decodeDtIdForDisplay", () => {
  it("percent-decodes the RDF node URI for readability", () => {
    expect(
      decodeDtIdForDisplay(
        "https://www.sbco.or.jp/ont/resource/building%3Asite%3Asite-1%2Fbldg-1",
      ),
    ).toBe("https://www.sbco.or.jp/ont/resource/building:site:site-1/bldg-1");
  });

  it("leaves an already-readable id unchanged", () => {
    expect(decodeDtIdForDisplay("urn:ngsi-ld:Building:bldg-1")).toBe(
      "urn:ngsi-ld:Building:bldg-1",
    );
  });

  it("returns the raw value when decoding fails (lone %)", () => {
    expect(decodeDtIdForDisplay("abc%def")).toBe("abc%def");
  });

  it("handles empty string", () => {
    expect(decodeDtIdForDisplay("")).toBe("");
  });
});
