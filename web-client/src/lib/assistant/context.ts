import { relatedTerms, resolveHelp } from "@/lib/help/resolve";
import type { HelpEntry } from "@/lib/help/types";
import type { AssistantHelpContext } from "./types";

/**
 * Pure builder of the assistant's screen context from a D-1 help key (#151). Reuses #149 content so
 * D-1 stays the single source of truth — no content is duplicated. Returns null when the key has no
 * bound help (the caller then sends no context).
 */
export function buildAssistantContext(
  helpKey: string,
  resolve: (key: string) => HelpEntry | null = resolveHelp,
): AssistantHelpContext | null {
  const entry = resolve(helpKey);
  if (!entry) return null;
  return {
    title: entry.title,
    body: entry.body,
    terms: relatedTerms(entry).map((t) => ({ term: t.term, definition: t.definition })),
  };
}

/** Whether the assistant UI is enabled (experimental/optional, off by default). */
export function isAssistantEnabled(): boolean {
  return process.env.NEXT_PUBLIC_ASSISTANT_ENABLED === "true";
}

/** Maps a route to the help key whose D-1 content becomes the assistant's screen context. */
const PATH_HELP_KEYS: Record<string, string> = {
  "/platform/status": "platform.status",
  "/platform/config": "platform.config",
  "/platform/settings": "platform.settings",
};

/** Resolves the current screen's help key from the pathname (exact or prefix), or null. */
export function helpKeyForPath(pathname: string): string | null {
  for (const [path, key] of Object.entries(PATH_HELP_KEYS)) {
    if (pathname === path || pathname.startsWith(`${path}/`)) return key;
  }
  return null;
}
