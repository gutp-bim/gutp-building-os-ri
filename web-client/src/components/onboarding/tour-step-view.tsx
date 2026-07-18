"use client";

import { Dialog } from "@/components/ui/dialog";
import type { ResolvedTourStep } from "@/lib/onboarding/types";

/**
 * Pure, presentational onboarding step (#150): step content + progress + navigation. The parent owns
 * the index and the skip/finish/persist behavior. The overlay/scrim + dialog a11y (focus trap,
 * Esc → skip, focus restoration) come from the shared {@link Dialog} primitive (#194). The tour is
 * non-dismissable (no click-outside-close) — it advances only via the explicit スキップ/戻る/次へ
 * controls; Esc still closes it the same way "スキップ" does.
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
  const isFirst = index === 0;
  const isLast = index === total - 1;
  return (
    <Dialog
      open
      onClose={onSkip}
      placement="center"
      dismissable={false}
      labelledBy="onboarding-tour-title"
      panelClassName="w-full max-w-lg rounded-lg bg-white p-6 shadow-xl"
      testId="onboarding-tour"
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
    </Dialog>
  );
}
