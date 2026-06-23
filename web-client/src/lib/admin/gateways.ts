import { API_BASE_URL, authHeaders, mutationError } from "./http";

/** Admin view of one gateway (`GET /api/admin/gateways`). Secret settings are masked server-side (#323). */
export interface GatewayAdminView {
  gatewayId: string;
  bindingType: string;
  settings: Record<string, string>;
  pointCount: number;
  revision: string;
  certTrustAnchor: string;
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

export async function fetchGateways(signal?: AbortSignal): Promise<GatewayAdminView[]> {
  const res = await fetch(`${API_BASE_URL}/api/admin/gateways`, { headers: authHeaders(), signal });
  if (!res.ok) throw new Error(`gateways request failed: ${res.status}`);
  return (await res.json()) as GatewayAdminView[];
}

/** Trigger a point-list resync push to the gateway. Returns the new revision. */
export async function resyncGatewayPointList(id: string): Promise<string> {
  const res = await fetch(
    `${API_BASE_URL}/api/admin/gateways/${encodeURIComponent(id)}/resync-pointlist`,
    { method: "POST", headers: authHeaders(true) },
  );
  if (!res.ok) throw await mutationError(res, "pointlist の再同期に失敗しました");
  return ((await res.json()) as { revision: string }).revision;
}
