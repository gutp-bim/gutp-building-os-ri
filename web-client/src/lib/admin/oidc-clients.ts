import { API_BASE_URL, authHeaders, mutationError } from "./http";

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

// ── Fetchers (bespoke admin fetch) ───────────────────────────────────────────

export async function fetchOidcClients(signal?: AbortSignal): Promise<OidcClientSummary[]> {
  const res = await fetch(`${API_BASE_URL}/api/admin/oidc-clients`, { headers: authHeaders(), signal });
  if (res.status === 503) throw new Error("OIDC クライアント管理は未設定です（Keycloak admin API）");
  if (!res.ok) throw new Error(`oidc clients request failed: ${res.status}`);
  return (await res.json()) as OidcClientSummary[];
}

export async function createOidcClient(req: CreateOidcClientRequest): Promise<CreatedOidcClient> {
  const res = await fetch(`${API_BASE_URL}/api/admin/oidc-clients`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify(req),
  });
  if (!res.ok) throw await mutationError(res, "クライアントの作成に失敗しました");
  return (await res.json()) as CreatedOidcClient;
}

export async function rotateOidcSecret(id: string): Promise<string> {
  const res = await fetch(`${API_BASE_URL}/api/admin/oidc-clients/${encodeURIComponent(id)}/rotate-secret`, {
    method: "POST",
    headers: authHeaders(true),
  });
  if (!res.ok) throw await mutationError(res, "シークレットの再生成に失敗しました");
  return ((await res.json()) as { secret: string }).secret;
}

export async function setOidcClientEnabled(id: string, enabled: boolean): Promise<OidcClientDetail> {
  const res = await fetch(`${API_BASE_URL}/api/admin/oidc-clients/${encodeURIComponent(id)}/enabled`, {
    method: "PUT",
    headers: authHeaders(true),
    body: JSON.stringify({ enabled }),
  });
  if (!res.ok) throw await mutationError(res, "有効/無効の切り替えに失敗しました");
  return (await res.json()) as OidcClientDetail;
}

export async function deleteOidcClient(id: string): Promise<void> {
  const res = await fetch(`${API_BASE_URL}/api/admin/oidc-clients/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: authHeaders(),
  });
  if (!res.ok) throw await mutationError(res, "クライアントの削除に失敗しました");
}
