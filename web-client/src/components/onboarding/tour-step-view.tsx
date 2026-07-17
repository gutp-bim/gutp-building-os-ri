"use client";

import { useRef } from "react";
import { useDialogA11y } from "@/lib/a11y/use-dialog-a11y";
import type { ResolvedTourStep } from "@/lib/onboarding/types";

/**
 * Pure, presentational onboarding step (#150): step content + progress + navigation. The parent owns
 * the index and the skip/finish/persist behavior. Only rendered while the tour is open, so dialog
 * a11y (focus trap, Esc → skip, focus restoration) binds to that lifetime via {@link useDialogA11y}
 * (#198). Esc closes the tour the same way "スキップ" does.
 */
export function TourStepView({
  step,
  index,
  total,
  onBack,
  onNext,
  onSkip,
}: {
  step: ResolvedTourStep;
  index: number;
  total: number;
  onBack: () => void;
  onNext: () => void;
  onSkip: () => void;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  useDialogA11y(panelRef, { open: true, onClose: onSkip });

  const isFirst = index === 0;
  const isLast = index === total - 1;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40" data-testid="onboarding-tour">
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="onboarding-tour-title"
        tabIndex={-1}
        className="w-full max-w-lg rounded-lg bg-white p-6 shadow-xl"
      >
        <div className="mb-1 text-xs text-gray-400" data-testid="tour-progress">
          {index + 1} / {total}
        </div>
        <h2 id="onboarding-tour-title" className="mb-3 text-lg font-bold">
          {step.title}
        </h2>
        <div className="mb-6 flex flex-col gap-2 text-sm text-gray-700">
          {step.body.map((para, i) => (
            <p key={i}>{para}</p>
          ))}
        </div>
        <div className="flex items-center justify-between">
          <button
            type="button"
            onClick={onSkip}
            className="text-sm text-gray-600 hover:underline"
          >
            スキップ
          </button>
          <div className="flex gap-2">
            <button
              type="button"
              onClick={onBack}
              disabled={isFirst}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-40"
            >
              戻る
            </button>
            <button
              type="button"
              onClick={onNext}
              className="rounded bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700"
            >
              {isLast ? "完了" : "次へ"}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
