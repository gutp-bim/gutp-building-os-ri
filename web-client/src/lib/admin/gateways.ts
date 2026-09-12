import { apiClient } from "@/lib/infra/aspida-client";
import { mutationError, requestError } from "./api-error";

/** Admin view of one gateway (`GET /api/admin/gateways`). Secret settings are masked server-side (#323). */
export interface GatewayAdminView {
  gatewayId: string;
  bindingType: string;
  settings: Record<string, string>;
  pointCount: number;
  revision: string;
  certTrustAnchor: string;
  /**
   * Derived last-seen: the most recent telemetry timestamp (ISO) across the gateway's points, or null
   * when none have reported (#181 Phase 2). This is the **ingress** last-seen, distinct from
   * {@link connected}.
   */
  lastTelemetryAt: string | null;
  /**
   * Live **egress** connection state (#230 Phase 2②, ADR-0004): true when a GatewayBridge replica is
   * holding a live egress control stream for this gateway right now (cross-replica NATS-KV heartbeat),
   * false when none is observed (TTL-expired/absent), `null` when the heartbeat could not be read at
   * all (#463 — 不明, not 未接続). Distinct from {@link lastTelemetryAt}: a gateway can be receiving
   * telemetry (ingress) yet have no egress stream, or vice-versa.
   */
  connected: boolean | null;
  /**
   * Pointlist sync state (#230 Phase 2b, ADR-0004 option A): compares the point-list ETag the gateway
   * reports as applied (via the egress stream) against the twin-authoritative {@link revision}.
   * `true` = in sync, `false` = drifted (a resync is warranted), `null` = unknown (the gateway has
   * not reported one — e.g. not connected, or a build predating the report).
   */
  pointlistSynced: boolean | null;
}

/**
 * Human label for the derived last-seen timestamp (#181 Phase 2). `null`/invalid → 「受信なし」;
 * otherwise a coarse relative age (秒/分/時間/日前).
 */
export function lastSeenLabel(
  iso: string | null | undefined,
  now: Date = new Date(),
): string {
  if (!iso) return "受信なし";
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return "受信なし";
  const diffSec = Math.max(0, Math.floor((now.getTime() - t) / 1000));
  if (diffSec < 60) return `${diffSec}秒前`;
  if (diffSec < 3600) return `${Math.floor(diffSec / 60)}分前`;
  if (diffSec < 86400) return `${Math.floor(diffSec / 3600)}時間前`;
  return `${Math.floor(diffSec / 86400)}日前`;
}

/**
 * Human label for the live egress connection state (#230). Tri-state (#463): `true` → 「接続中」,
 * `false` → 「未接続」, `null` → 「接続状態不明」 — the heartbeat could not be read, which says nothing
 * about whether the gateway is up. Same shape as {@link pointlistSyncedLabel} next to it.
 */
export function connectedLabel(connected: boolean | null): string {
  if (connected === null) return "接続状態不明";
  return connected ? "接続中" : "未接続";
}

/** Tone for the pointlist sync badge (#230 Phase 2b): drives the badge colour without inline logic. */
export type PointlistSyncTone = "ok" | "warn" | "unknown";

/**
 * Tone for the connection badge (#463). Its own type rather than {@link PointlistSyncTone}: the two
 * badges sit side by side but do not share a palette — 未接続 is red (an outage), while a drifted
 * pointlist is amber. The three values must render in three different colours; painting 接続状態不明
 * the same grey-or-otherwise as 未接続 hides the state the tri-state exists to show. Matches the
 * palette `GatewayConnectionBadge` already uses on `/health` so the same fact looks the same on both
 * screens.
 */
export type GatewayConnectionTone = "connected" | "disconnected" | "unknown";

export function connectedTone(connected: boolean | null): GatewayConnectionTone {
  if (connected === null) return "unknown";
  return connected ? "connected" : "disconnected";
}

/**
 * Tri-state pointlist sync presentation (#230 Phase 2b). `true` → 同期済み (ok),
 * `false` → 未同期 (warn — resync warranted), `null` → 不明 (unknown, e.g. not connected / not reported).
 */
export function pointlistSyncedLabel(synced: boolean | null): string {
  if (synced === null) return "同期状態不明";
  return synced ? "同期済み" : "未同期";
}

export function pointlistSyncedTone(synced: boolean | null): PointlistSyncTone {
  if (synced === null) return "unknown";
  return synced ? "ok" : "warn";
}

export function bindingLabel(binding: string): string {
  switch (binding) {
    case "hono":
      return "Hono (AMQP)";
    case "kandt":
      return "Kandt (IoT Hub)";
    case "bacnet-sim":
      return "BACnet Sim";
    case "simulated":
      return "Simulated";
    default:
      return binding;
  }
}

/** Short form of the content-hash revision for display (the ETag is "sha256:…"). */
export function shortRevision(revision: string): string {
  const hex = revision.startsWith("sha256:") ? revision.slice(7) : revision;
  return hex ? hex.slice(0, 12) : "—";
}

export async function fetchGateways(
  signal?: AbortSignal,
): Promise<GatewayAdminView[]> {
  try {
    return (await apiClient().api.admin.gateways.$get({
      config: { signal },
    })) as GatewayAdminView[];
  } catch (e) {
    throw requestError(e, "gateways request failed");
  }
}

/** Trigger a point-list resync push to the gateway. Returns the new revision. */
export async function resyncGatewayPointList(id: string): Promise<string> {
  try {
    // Swagger documents the 202 without a body, so the generated method is typed void — the server
    // does return `{ revision }` (see GatewayAdminController); read it from the raw response.
    const res = await apiClient()
      .api.admin.gateways._id(encodeURIComponent(id))
      .resync_pointlist.post();
    return (res.body as unknown as { revision: string }).revision;
  } catch (e) {
    throw mutationError(e, "pointlist の再同期に失敗しました");
  }
}
