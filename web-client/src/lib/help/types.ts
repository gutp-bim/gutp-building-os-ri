/**
 * Content-as-code help foundation (#149). Knowledge is small and fixed, so it lives as typed TS data
 * in the repo (i18n = ja) — no RAG/runtime fetch. This content is the single source of truth and is
 * also intended as the prompt material for a future Tier-1 LLM helper.
 */

/** A glossary term — a `bos:` vocabulary word or the meaning of a metric. */
export interface GlossaryTerm {
  /** The canonical term id, used for lookup and as the display label. */
  term: string;
  /** Optional reading (ふりがな) for the term. */
  reading?: string;
  definition: string;
  category: string;
}

/** A help entry bound to a screen/component via its stable {@link key}. Body is plain paragraphs. */
export interface HelpEntry {
  key: string;
  title: string;
  /** Body as an ordered list of plain-text paragraphs (no markdown — deterministic, XSS-free). */
  body: string[];
  /** Glossary term ids related to this entry (resolved to definitions in the drawer). */
  relatedTerms?: string[];
}
