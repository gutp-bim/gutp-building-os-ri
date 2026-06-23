import { GLOSSARY, HELP_ENTRIES } from "./content";
import type { GlossaryTerm, HelpEntry } from "./types";

/**
 * Pure resolution over the help content (#149). The registries are injectable (default to the seed)
 * so the logic is fully unit-testable with custom fixtures.
 */

/** Resolves a help entry by its stable key, or null when none is bound. */
export function resolveHelp(key: string, entries: HelpEntry[] = HELP_ENTRIES): HelpEntry | null {
  return entries.find((e) => e.key === key) ?? null;
}

/** Resolves a glossary term by name (case-insensitive), or null when unknown. */
export function resolveTerm(term: string, glossary: GlossaryTerm[] = GLOSSARY): GlossaryTerm | null {
  const needle = term.trim().toLowerCase();
  return glossary.find((g) => g.term.toLowerCase() === needle) ?? null;
}

/** Resolves an entry's related term ids to glossary terms, skipping any that are unknown. */
export function relatedTerms(entry: HelpEntry, glossary: GlossaryTerm[] = GLOSSARY): GlossaryTerm[] {
  return (entry.relatedTerms ?? [])
    .map((t) => resolveTerm(t, glossary))
    .filter((g): g is GlossaryTerm => g !== null);
}
