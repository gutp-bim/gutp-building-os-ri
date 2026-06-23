/**
 * Pure display helpers for groups (#143). Resource-type badge colours are shared with permission
 * display (see {@link resourceTypeColor} in permissions-display).
 */

/** Formats an ISO timestamp as a ja-JP date, or "—" when missing/unparseable. */
export function formatDate(iso?: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleDateString("ja-JP");
}
