import { describe, expect, it } from "vitest";
import { GLOSSARY, HELP_ENTRIES } from "./content";
import { relatedTerms, resolveTerm } from "./resolve";

/**
 * Structural-integrity guards over the seeded content-as-code (#149). These protect the invariants
 * the resolution logic and UI rely on as the glossary/help arrays grow.
 */
describe("GLOSSARY integrity", () => {
  it("has no terms that collide under resolveTerm's trim+lowercase normalization", () => {
    // resolveTerm matches on term.trim().toLowerCase(); two entries normalizing to the same key
    // would make one permanently unreachable.
    const keys = GLOSSARY.map((g) => g.term.trim().toLowerCase());
    expect(new Set(keys).size).toBe(keys.length);
  });

  it("gives every term a non-empty term id and definition", () => {
    for (const g of GLOSSARY) {
      expect(g.term.trim()).not.toBe("");
      expect(g.definition.trim()).not.toBe("");
    }
  });
});

describe("HELP_ENTRIES integrity", () => {
  it("has unique, non-empty keys", () => {
    const keys = HELP_ENTRIES.map((e) => e.key);
    for (const k of keys) expect(k.trim()).not.toBe("");
    expect(new Set(keys).size).toBe(keys.length);
  });

  it("only references relatedTerms that resolve to a real glossary term", () => {
    // relatedTerms silently drops unknown ids, so a typo would vanish from the UI with no error.
    for (const entry of HELP_ENTRIES) {
      const ids = entry.relatedTerms ?? [];
      const resolved = relatedTerms(entry);
      expect(resolved.length).toBe(ids.length);
    }
  });
});

describe("core concept terms (#160)", () => {
  // The onboarding-friendliness gap the review flagged: newcomers hit these terms with no in-app
  // explanation. Each must resolve from the default registry with a substantive definition.
  const CONCEPT_TERMS = [
    "デジタルツイン",
    "リソース",
    "ゲートウェイ",
    "localId",
    "point_id",
    "gateway_id",
    "device_id",
    "GatewayIngress",
    "GatewayEgress",
    "ポイントリスト",
    "リビジョン",
    "SBCO",
    "OxiGraph",
    "階層ストレージ",
  ];

  it.each(CONCEPT_TERMS)("resolves %s with a definition", (term) => {
    const resolved = resolveTerm(term);
    expect(resolved, `glossary is missing "${term}"`).not.toBeNull();
    expect(resolved!.definition.length).toBeGreaterThan(10);
  });

  it("resolves localId case-insensitively (matches resolveTerm contract)", () => {
    expect(resolveTerm("localid")?.term).toBe("localId");
  });
});
