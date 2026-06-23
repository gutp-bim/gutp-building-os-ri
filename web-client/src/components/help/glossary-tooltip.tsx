import { resolveTerm } from "@/lib/help/resolve";

/**
 * Inline glossary term with its definition surfaced as a native tooltip (#149). When the term is
 * unknown, the children render unchanged (no decoration).
 */
export function GlossaryTooltip({ term, children }: { term: string; children?: React.ReactNode }) {
  const resolved = resolveTerm(term);
  const label = children ?? term;
  if (!resolved) {
    return <>{label}</>;
  }
  return (
    <span
      title={resolved.definition}
      data-testid={`glossary-${term}`}
      className="cursor-help underline decoration-dotted underline-offset-2"
    >
      {label}
    </span>
  );
}
