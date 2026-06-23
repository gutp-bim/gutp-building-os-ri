import type { ConfigEntry } from "./types";

/**
 * Pure display helpers for the effective-config view (#147). Secret entries never expose a value, so
 * they render as presence-only ("設定済み" / "未設定"). Non-secret entries show their value, or
 * "未設定" when unset.
 */
export function configValueDisplay(entry: ConfigEntry): string {
  if (entry.isSecret) {
    return entry.isSet ? "設定済み" : "未設定";
  }
  if (!entry.isSet || entry.value == null || entry.value === "") {
    return "未設定";
  }
  return entry.value;
}

/** Whether the value cell should be rendered as a muted "未設定" placeholder. */
export function isUnset(entry: ConfigEntry): boolean {
  return !entry.isSet;
}
