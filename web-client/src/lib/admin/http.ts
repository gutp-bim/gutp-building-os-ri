import Cookies from "js-cookie";

/**
 * Base URL + auth/error helpers for the remaining bespoke-fetch callers (#143). The admin API
 * modules in this directory now use the generated aspida client (#38, see `api-error.ts` for their
 * error translation); the only consumer left is the resource-metadata write path in
 * `src/lib/resources/repository.ts`.
 */
export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";

export function authHeaders(json = false): HeadersInit {
  const headers: Record<string, string> = {
    Authorization: `Bearer ${Cookies.get("oidc.access_token") || ""}`,
  };
  if (json) headers["Content-Type"] = "application/json";
  return headers;
}

/** Builds an Error from a failed mutation response, preferring the API's plain-text/JSON body. */
export async function mutationError(res: Response, fallback: string): Promise<Error> {
  let detail = "";
  try {
    detail = (await res.text()).trim();
  } catch {
    // ignore — body may be empty
  }
  return new Error(detail || `${fallback} (${res.status})`);
}
