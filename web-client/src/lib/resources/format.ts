/**
 * Display-layer formatting for resource identifiers. Backward-compatible: the raw {@link
 * ResourceRef.dtId} (RDF node URI) is still used for routing / keys / API — this only prettifies the
 * string shown to the user. dtId is percent-encoded because the business id contains reserved chars
 * (`:` `/`) embedded in an http(s) IRI; decoding makes it readable without changing the value.
 */
export function decodeDtIdForDisplay(dtId: string): string {
  try {
    return decodeURIComponent(dtId);
  } catch {
    // Malformed escape (e.g. a lone '%') — fall back to the raw value rather than throwing.
    return dtId;
  }
}
