import { describe, expect, it } from "vitest";
import type { HelpEntry } from "@/lib/help/types";
import { buildAssistantContext, helpKeyForPath, isAssistantEnabled } from "./context";

const fakeHelp = (key: string): HelpEntry | null =>
  key === "platform.settings"
    ? { key, title: "アプリ設定", body: ["一行目"], relatedTerms: ["鮮度切れ閾値"] }
    : null;

describe("buildAssistantContext", () => {
  it("builds context from a D-1 help entry (reusing #149 content)", () => {
    const ctx = buildAssistantContext("platform.settings", fakeHelp);
    expect(ctx?.title).toBe("アプリ設定");
    expect(ctx?.body).toEqual(["一行目"]);
    // related glossary term resolved from the default glossary
    expect(ctx?.terms.some((t) => t.term === "鮮度切れ閾値")).toBe(true);
  });

  it("returns null when the key has no bound help", () => {
    expect(buildAssistantContext("nope", fakeHelp)).toBeNull();
  });
});

describe("isAssistantEnabled", () => {
  it("is false unless NEXT_PUBLIC_ASSISTANT_ENABLED is 'true'", () => {
    // not set in test env → disabled by default
    expect(isAssistantEnabled()).toBe(false);
  });
});

describe("helpKeyForPath", () => {
  it("resolves platform routes (exact and nested)", () => {
    expect(helpKeyForPath("/platform/status")).toBe("platform.status");
    expect(helpKeyForPath("/platform/settings")).toBe("platform.settings");
    expect(helpKeyForPath("/platform/config/extra")).toBe("platform.config");
  });

  it("returns null for unmapped routes", () => {
    expect(helpKeyForPath("/buildings")).toBeNull();
  });
});
