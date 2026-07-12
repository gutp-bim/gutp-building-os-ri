import { isAxiosError } from "axios";

/**
 * Error translation for the aspida/axios-backed admin API layer (#18 Phase 2 / #38). Mirrors the
 * behavior the bespoke-fetch modules had when they built errors from `Response` objects:
 * - reads throw `"<label>: <status>"` on an HTTP error,
 * - mutations prefer the response body (plain-text or JSON) then fall back to `"<fallback> (<status>)"`,
 * - an aborted request surfaces as an `AbortError` (components filter on `e.name === "AbortError"`),
 * - network errors without a response are passed through unchanged.
 */

/** Axios cancellation → the DOM-style AbortError the fetch implementation used to throw. */
function abortError(): Error {
  const e = new Error("The operation was aborted.");
  e.name = "AbortError";
  return e;
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

/** Extracts the response body of a failed request as display text ("" when empty/unreadable). */
function responseDetail(data: unknown): string {
  if (typeof data === "string") return data.trim();
  if (data === null || data === undefined) return "";
  try {
    return JSON.stringify(data);
  } catch {
    return "";
  }
}

/** Builds the Error a failed read (GET) should throw: `"<label>: <status>"`. */
export function requestError(error: unknown, label: string): Error {
  if (isAxiosError(error)) {
    if (error.code === "ERR_CANCELED") return abortError();
    if (error.response) return new Error(`${label}: ${error.response.status}`);
  }
  return asError(error);
}

/** Builds the Error a failed mutation should throw, preferring the API's plain-text/JSON body. */
export function mutationError(error: unknown, fallback: string): Error {
  if (isAxiosError(error)) {
    if (error.code === "ERR_CANCELED") return abortError();
    if (error.response) {
      const detail = responseDetail(error.response.data);
      return new Error(detail || `${fallback} (${error.response.status})`);
    }
  }
  return asError(error);
}

/** Status of the failed response, or undefined for non-HTTP failures (network error, abort). */
export function errorStatus(error: unknown): number | undefined {
  return isAxiosError(error) ? error.response?.status : undefined;
}
