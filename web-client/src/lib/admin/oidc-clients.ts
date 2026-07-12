import { apiClient } from "@/lib/infra/aspida-client";
import { errorStatus, mutationError, requestError } from "./api-error";

/** OIDC client summary (`GET /api/admin/oidc-clients`). Never carries the secret (#324). */
export interface OidcClientSummary {
  id: string;
  clientId: string;
  enabled: boolean;
  serviceAccountsEnabled: boolean;
  description?: string | null;
}

export interface OidcClientDetail extends OidcClientSummary {
  publicClient: boolean;
  redirectUris: string[];
}

export interface CreateOidcClientRequest {
  clientId: string;
  description?: string;
  serviceAccountsEnabled: boolean;
  redirectUris?: string[];
}

/** Create response — the plaintext `secret` is returned ONCE and never readable again. */
export interface CreatedOidcClient {
  client: OidcClientDetail;
  secret: string;
}

// ── Pure display helpers ─────────────────────────────────────────────────────

export function clientStatusLabel(c: { enabled: boolean }): string {
  return c.enabled ? "有効" : "無効";
}

export function clientStatusBadgeClass(c: { enabled: boolean }): string {
  return c.enabled ? "bg-green-100 text-green-800" : "bg-gray-200 text-gray-600";
}

/** Service-account (machine-to-machine) vs standard (interactive) client. */
export function clientTypeLabel(c: { serviceAccountsEnabled: boolean }): string {
  return c.serviceAccountsEnabled ? "サービスアカウント" : "標準クライアント";
}

// ── Fetchers (generated aspida client) ───────────────────────────────────────

export async function fetchOidcClients(signal?: AbortSignal): Promise<OidcClientSummary[]> {
  try {
    return (await apiClient().api.admin.oidc_clients.$get({
      config: { signal },
    })) as OidcClientSummary[];
  } catch (e) {
    if (errorStatus(e) === 503) {
      throw new Error("OIDC クライアント管理は未設定です（Keycloak admin API）");
    }
    throw requestError(e, "oidc clients request failed");
  }
}

export async function createOidcClient(req: CreateOidcClientRequest): Promise<CreatedOidcClient> {
  try {
    return (await apiClient().api.admin.oidc_clients.$post({ body: req })) as CreatedOidcClient;
  } catch (e) {
    throw mutationError(e, "クライアントの作成に失敗しました");
  }
}

export async function rotateOidcSecret(id: string): Promise<string> {
  try {
    const res = await apiClient()
      .api.admin.oidc_clients._id(encodeURIComponent(id))
      .rotate_secret.$post();
    return (res as { secret: string }).secret;
  } catch (e) {
    throw mutationError(e, "シークレットの再生成に失敗しました");
  }
}

export async function setOidcClientEnabled(id: string, enabled: boolean): Promise<OidcClientDetail> {
  try {
    return (await apiClient().api.admin.oidc_clients._id(encodeURIComponent(id)).enabled.$put({
      body: { enabled },
    })) as OidcClientDetail;
  } catch (e) {
    throw mutationError(e, "有効/無効の切り替えに失敗しました");
  }
}

export async function deleteOidcClient(id: string): Promise<void> {
  try {
    await apiClient().api.admin.oidc_clients._id(encodeURIComponent(id)).$delete();
  } catch (e) {
    throw mutationError(e, "クライアントの削除に失敗しました");
  }
}
