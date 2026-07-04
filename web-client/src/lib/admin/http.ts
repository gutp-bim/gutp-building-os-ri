import Cookies from "js-cookie";

/** Shared base URL + auth/error helpers for the admin bespoke-fetch clients (#143). */
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
