import type { PointFreshness } from "@/lib/telemetry/freshness";

/**
 * A point id paired with its human-readable name and (optionally) the twin context an operator needs
 * to locate it — the owning device (equipment) and space (room). Populated by the floor traversal.
 */
export type NamedPoint = {
  pointId: string;
  name: string;
  deviceName?: string;
  spaceName?: string;
  /** Expected telemetry interval (seconds) for per-point stale detection (#183); undefined = unknown. */
  expectedIntervalSeconds?: number | null;
};

/** A point needing operator attention: stale (old) or missing (never/unparseable). */
export type AttentionItem = {
  pointId: string;
  name: string;
  status: "stale" | "missing";
  ageSeconds: number | null;
  deviceName?: string;
  spaceName?: string;
};

/**
 * Build the operator "needs attention" list from per-point freshness: keep only stale/missing
 * points, join their twin context (name + device + space), and sort worst-first — missing points
 * before stale, then stale by age descending (oldest first). Pure and deterministic (unit-tested).
 */
export function buildAttentionList(
  named: NamedPoint[],
  freshness: PointFreshness[],
): AttentionItem[] {
  const byId = new Map(named.map((n) => [n.pointId, n]));

  const items: AttentionItem[] = freshness
    .filter((f): f is PointFreshness & { status: "stale" | "missing" } =>
      f.status === "stale" || f.status === "missing",
    )
    .map((f) => {
      const meta = byId.get(f.pointId);
      return {
        pointId: f.pointId,
        name: meta?.name ?? f.pointId,
        status: f.status,
        ageSeconds: f.ageSeconds,
        deviceName: meta?.deviceName,
        spaceName: meta?.spaceName,
      };
    });

  return items.sort((a, b) => {
    if (a.status !== b.status) return a.status === "missing" ? -1 : 1;
    if (a.status === "missing") return a.name.localeCompare(b.name);
    // both stale: oldest (largest age) first; nulls last
    return (b.ageSeconds ?? -1) - (a.ageSeconds ?? -1);
  });
}
