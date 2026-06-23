import { describe, expect, it } from "vitest";
import { relatedTerms, resolveHelp, resolveTerm } from "./resolve";
import type { GlossaryTerm, HelpEntry } from "./types";

const glossary: GlossaryTerm[] = [
  { term: "ポイント", definition: "計測点", category: "ontology" },
  { term: "MsgRate", definition: "rate", category: "metric" },
];

const entries: HelpEntry[] = [
  { key: "screen.a", title: "A", body: ["one", "two"], relatedTerms: ["ポイント", "unknown"] },
];

describe("resolveHelp", () => {
  it("finds an entry by key, null otherwise", () => {
    expect(resolveHelp("screen.a", entries)?.title).toBe("A");
    expect(resolveHelp("missing", entries)).toBeNull();
  });

  it("resolves seeded screen keys from the default registry", () => {
    expect(resolveHelp("platform.settings")?.title).toBe("アプリ設定");
  });
});

describe("resolveTerm", () => {
  it("is case-insensitive and trims", () => {
    expect(resolveTerm("  msgrate ", glossary)?.term).toBe("MsgRate");
    expect(resolveTerm("ポイント", glossary)?.definition).toBe("計測点");
  });

  it("returns null for an unknown term", () => {
    expect(resolveTerm("nope", glossary)).toBeNull();
  });
});

describe("relatedTerms", () => {
  it("resolves known related terms and skips unknown ones", () => {
    const result = relatedTerms(entries[0], glossary);
    expect(result.map((t) => t.term)).toEqual(["ポイント"]);
  });

  it("returns an empty array when there are no related terms", () => {
    expect(relatedTerms({ key: "x", title: "x", body: [] }, glossary)).toEqual([]);
  });
});
