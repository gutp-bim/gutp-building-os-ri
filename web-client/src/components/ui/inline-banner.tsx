import type { ReactNode } from "react";

export type BannerTone = "error" | "warn" | "success" | "info";

const TONE_STYLES: Record<BannerTone, string> = {
  error: "bg-red-50 border-red-300 text-red-800",
  warn: "bg-amber-50 border-amber-300 text-amber-800",
  success: "bg-green-50 border-green-300 text-green-800",
  info: "bg-blue-50 border-blue-300 text-blue-800",
};

/**
 * Inline status banner (#196): the "取得系はインライン" half of the notification policy — a small,
 * in-flow alert for fetch/read failures and success confirmations, so those stop falling silently to
 * `console.error`. Presentational and dependency-free (no toast library); a natural primitive for the
 * shared `components/ui` layer (#194) to absorb. `error` uses `role="alert"` (assertive), the rest
 * `role="status"` (polite).
 */
export function InlineBanner({
  tone,
  children,
  onDismiss,
  testId,
}: {
  tone: BannerTone;
  children: ReactNode;
  onDismiss?: () => void;
  testId?: string;
}) {
  return (
    <div
      role={tone === "error" ? "alert" : "status"}
      data-testid={testId ?? `inline-banner-${tone}`}
      className={`flex items-start justify-between gap-3 rounded-md border px-3 py-2 text-sm ${TONE_STYLES[tone]}`}
    >
      <div>{children}</div>
      {onDismiss && (
        <button
          type="button"
          aria-label="閉じる"
          onClick={onDismiss}
          className="shrink-0 text-lg leading-none opacity-70 hover:opacity-100"
        >
          ×
        </button>
      )}
    </div>
  );
}
