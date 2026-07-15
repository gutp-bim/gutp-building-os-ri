import type { PointFreshness } from "@/lib/telemetry/freshness";

/** A point id paired with its human-readable name (from the twin). */
export type NamedPoint = { pointId: string; name: string };

/** A point needing operator attention: stale (old) or missing (never/unparseable). */
export type AttentionItem = {
  pointId: string;
  name: string;
  status: "stale" | "missing";
  ageSeconds: number | null;
};

/**
 * Build the operator "needs attention" list from per-point freshness: keep only stale/missing
 * points, join their names, and sort worst-first — missing points before stale, then stale by
 * age descending (oldest first). Pure and deterministic (unit-tested).
 */
export function buildAttentionList(
  named: NamedPoint[],
  freshness: PointFreshness[],
): AttentionItem[] {
  const nameById = new Map(named.map((n) => [n.pointId, n.name]));

  const items: AttentionItem[] = freshness
    .filter((f): f is PointFreshness & { status: "stale" | "missing" } =>
      f.status === "stale" || f.status === "missing",
    )
    .map((f) => ({
      pointId: f.pointId,
      name: nameById.get(f.pointId) ?? f.pointId,
      status: f.status,
      ageSeconds: f.ageSeconds,
    }));

  return items.sort((a, b) => {
    if (a.status !== b.status) return a.status === "missing" ? -1 : 1;
    if (a.status === "missing") return a.name.localeCompare(b.name);
    // both stale: oldest (largest age) first; nulls last
    return (b.ageSeconds ?? -1) - (a.ageSeconds ?? -1);
  });
}
