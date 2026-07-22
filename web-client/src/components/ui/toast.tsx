"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { TONE_STYLES, type BannerTone } from "./inline-banner";

/**
 * Transient toast notifications — the "toast" half of the unified notification policy (#162):
 *
 * - **toast**（本コンポーネント）: 一過性のフィードバック — 操作の**成功**や一過性の通信エラーなど、
 *   数秒で消えてよい合図。自動消滅（既定 5s）。`useToast().showToast(...)`。
 * - **InlineBanner**（`./inline-banner`, #196）: 消えると困る**説明** — 取得失敗・権限不足・
 *   バリデーション・gateway offline など。in-flow で永続表示（手動 dismiss）。
 *
 * ポリシー詳細は `docs/architecture/oss-frontend-notification-policy.md`。依存ライブラリなし（InlineBanner と同方針）で、
 * `AppShell` に一度だけマウントする。
 */
export type Toast = {
  id: number;
  tone: BannerTone;
  message: string;
};

type ShowToast = (
  message: string,
  opts?: { tone?: BannerTone; durationMs?: number },
) => void;

const ToastContext = createContext<{ showToast: ShowToast } | null>(null);

const DEFAULT_DURATION_MS = 5000;

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const idRef = useRef(0);
  const timers = useRef<Map<number, ReturnType<typeof setTimeout>>>(new Map());

  const dismiss = useCallback((id: number) => {
    setToasts((ts) => ts.filter((t) => t.id !== id));
    const timer = timers.current.get(id);
    if (timer) {
      clearTimeout(timer);
      timers.current.delete(id);
    }
  }, []);

  const showToast = useCallback<ShowToast>(
    (message, opts) => {
      const id = ++idRef.current;
      const tone = opts?.tone ?? "success";
      setToasts((ts) => [...ts, { id, tone, message }]);
      const duration = opts?.durationMs ?? DEFAULT_DURATION_MS;
      if (duration > 0) {
        timers.current.set(
          id,
          setTimeout(() => dismiss(id), duration),
        );
      }
    },
    [dismiss],
  );

  // Clear any pending timers on unmount.
  useEffect(() => {
    const map = timers.current;
    return () => {
      map.forEach((t) => clearTimeout(t));
      map.clear();
    };
  }, []);

  return (
    <ToastContext.Provider value={{ showToast }}>
      {children}
      <ToastViewport toasts={toasts} onDismiss={dismiss} />
    </ToastContext.Provider>
  );
}

export function useToast(): { showToast: ShowToast } {
  const ctx = useContext(ToastContext);
  if (!ctx) {
    throw new Error("useToast must be used within a <ToastProvider>");
  }
  return ctx;
}

function ToastViewport({
  toasts,
  onDismiss,
}: {
  toasts: Toast[];
  onDismiss: (id: number) => void;
}) {
  return (
    <div
      className="pointer-events-none fixed bottom-4 right-4 z-50 flex w-full max-w-sm flex-col gap-2"
      aria-live="polite"
      aria-atomic="false"
    >
      {toasts.map((t) => (
        <div
          key={t.id}
          role={t.tone === "error" ? "alert" : "status"}
          data-testid={`toast-${t.tone}`}
          className={`pointer-events-auto flex items-start justify-between gap-3 rounded-md border px-3 py-2 text-sm shadow-md ${TONE_STYLES[t.tone]}`}
        >
          <div>{t.message}</div>
          <button
            type="button"
            aria-label="閉じる"
            onClick={() => onDismiss(t.id)}
            className="shrink-0 opacity-70 hover:opacity-100"
          >
            <svg
              className="h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2}
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M6 18L18 6M6 6l12 12"
              />
            </svg>
          </button>
        </div>
      ))}
    </div>
  );
}
