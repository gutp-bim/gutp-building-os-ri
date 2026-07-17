"use client";

import { useRef } from "react";
import { useDialogA11y } from "@/lib/a11y/use-dialog-a11y";
import type { GlossaryTerm, HelpEntry } from "@/lib/help/types";

/**
 * Slide-over help drawer (#149). Pure/presentational: given a resolved {@link HelpEntry} and its
 * related glossary terms, renders the title, body paragraphs and term definitions. Open/close state
 * is owned by the parent. Dialog a11y (focus trap, Esc, focus restoration) via {@link useDialogA11y}
 * (#198).
 */
export function HelpDrawer({
  entry,
  terms,
  open,
  onClose,
}: {
  entry: HelpEntry | null;
  terms: GlossaryTerm[];
  open: boolean;
  onClose: () => void;
}) {
  const panelRef = useRef<HTMLElement>(null);
  useDialogA11y(panelRef, { open, onClose });

  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex justify-end" data-testid="help-drawer">
      <button
        type="button"
        aria-label="ヘルプを閉じる"
        className="flex-1 bg-black/30"
        onClick={onClose}
      />
      <aside
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={entry ? "help-drawer-title" : undefined}
        aria-label={entry ? undefined : "ヘルプ"}
        tabIndex={-1}
        className="flex w-full max-w-md flex-col overflow-auto bg-white p-5 shadow-xl"
      >
        {entry === null ? (
          <p className="text-sm text-gray-600" data-testid="help-missing">
            この画面のヘルプはまだありません。
          </p>
        ) : (
          <>
            <div className="mb-3 flex items-start justify-between gap-2">
              <h2 id="help-drawer-title" className="text-lg font-bold">
                {entry.title}
              </h2>
              <button
                type="button"
                onClick={onClose}
                className="text-2xl leading-none text-gray-500 hover:text-gray-700"
                aria-label="閉じる"
              >
                ×
              </button>
            </div>
            <div className="flex flex-col gap-2 text-sm text-gray-700">
              {entry.body.map((para, i) => (
                <p key={i}>{para}</p>
              ))}
            </div>
            {terms.length > 0 && (
              <div className="mt-5">
                <h3 className="mb-2 text-sm font-semibold">関連用語</h3>
                <dl className="flex flex-col gap-2" data-testid="help-related-terms">
                  {terms.map((t) => (
                    <div key={t.term}>
                      <dt className="text-sm font-medium">
                        {t.term}
                        {t.reading && <span className="ml-1 text-xs text-gray-400">{t.reading}</span>}
                      </dt>
                      <dd className="text-sm text-gray-600">{t.definition}</dd>
                    </div>
                  ))}
                </dl>
              </div>
            )}
          </>
        )}
      </aside>
    </div>
  );
}
