"use client";

import { useRef, useState } from "react";
import { useDialogA11y } from "@/lib/a11y/use-dialog-a11y";
import type { ChatMessage } from "@/lib/assistant/types";

/**
 * Presentational chat panel (#151): message transcript + a single-line composer. Read-only Q&A — it
 * only sends/shows text. The parent owns the conversation state and the network call.
 *
 * This is a **non-modal** floating helper: it deliberately does not cover the app, so it must not
 * trap focus or claim `aria-modal` (#198 review). `useDialogA11y({ modal: false })` still gives it
 * initial focus, Esc-to-close, and focus restoration, but lets focus leave freely.
 */
export function AssistantPanel({
  messages,
  busy,
  error,
  onSend,
  onClose,
}: {
  messages: ChatMessage[];
  busy?: boolean;
  error?: string | null;
  onSend: (text: string) => void;
  onClose: () => void;
}) {
  const [draft, setDraft] = useState("");
  const panelRef = useRef<HTMLDivElement>(null);
  useDialogA11y(panelRef, { open: true, onClose, modal: false });

  const submit = () => {
    const text = draft.trim();
    if (!text || busy) return;
    onSend(text);
    setDraft("");
  };

  return (
    <div
      ref={panelRef}
      role="dialog"
      aria-labelledby="assistant-panel-title"
      tabIndex={-1}
      className="fixed bottom-4 right-4 z-50 flex h-[28rem] w-96 flex-col rounded-lg border border-gray-200 bg-white shadow-xl"
      data-testid="assistant-panel"
    >
      <div className="flex items-center justify-between border-b p-3">
        <h2 id="assistant-panel-title" className="text-sm font-semibold">
          ヘルプアシスタント（実験的）
        </h2>
        <button type="button" onClick={onClose} aria-label="閉じる" className="text-xl leading-none text-gray-500 hover:text-gray-700">
          ×
        </button>
      </div>

      <div className="flex-1 space-y-2 overflow-auto p-3" data-testid="assistant-messages">
        {messages.length === 0 && (
          <p className="text-xs text-gray-400">
            この画面の使い方を質問できます。操作（制御・設定変更）は行いません。
          </p>
        )}
        {messages.map((m, i) => (
          <div
            key={i}
            className={m.role === "user" ? "text-right" : "text-left"}
            data-testid={`assistant-msg-${m.role}`}
          >
            <span
              className={`inline-block rounded px-2 py-1 text-sm ${
                m.role === "user" ? "bg-blue-600 text-white" : "bg-gray-100 text-gray-800"
              }`}
            >
              {m.content}
            </span>
          </div>
        ))}
        {busy && <p className="text-xs text-gray-400">考え中…</p>}
        {error && (
          <p className="text-xs text-red-600" data-testid="assistant-error">
            {error}
          </p>
        )}
      </div>

      <div className="flex gap-2 border-t p-3">
        <input
          aria-label="質問を入力"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") submit();
          }}
          placeholder="使い方を質問…"
          className="flex-1 rounded border border-gray-300 px-2 py-1 text-sm"
        />
        <button
          type="button"
          onClick={submit}
          disabled={busy}
          className="rounded bg-blue-600 px-3 py-1 text-sm text-white hover:bg-blue-700 disabled:opacity-50"
        >
          送信
        </button>
      </div>
    </div>
  );
}
