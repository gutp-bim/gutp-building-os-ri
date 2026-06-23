import type { SettingType } from "./types";

/**
 * Pure client-side validation mirroring the server (#148) for instant feedback before PUT. The server
 * remains authoritative; this only avoids obviously-invalid requests and normalizes input.
 */
export type SettingInputValidation = { ok: true; normalized: string } | { ok: false; error: string };

export function validateSettingInput(type: SettingType, value: string): SettingInputValidation {
  const raw = value.trim();
  switch (type) {
    case "Boolean": {
      const lower = raw.toLowerCase();
      if (lower === "true" || lower === "false") return { ok: true, normalized: lower };
      return { ok: false, error: "真偽値（true/false）が必要です" };
    }
    case "Number": {
      if (raw !== "" && Number.isFinite(Number(raw))) {
        return { ok: true, normalized: String(Number(raw)) };
      }
      return { ok: false, error: "数値が必要です" };
    }
    case "String":
    default:
      return { ok: true, normalized: value };
  }
}
