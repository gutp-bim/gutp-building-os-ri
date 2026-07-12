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
    const res = await apiClient().api.admin.gateways._id(encodeURIComponent(id)).resync_pointlist.post();
    return (res.body as unknown as { revision: string }).revision;
  } catch (e) {
    throw mutationError(e, "pointlist の再同期に失敗しました");
  }
}
