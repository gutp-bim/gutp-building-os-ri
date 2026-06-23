"use client";

import { REPLAY_EVENT, resetTour } from "@/lib/onboarding/storage";

/**
 * "ガイドを再表示" control (#150): clears the completed flag and asks the mounted
 * {@link OnboardingTour} to re-open via {@link REPLAY_EVENT}.
 */
export function ReplayTourButton({ className }: { className?: string }) {
  const replay = () => {
    resetTour();
    window.dispatchEvent(new Event(REPLAY_EVENT));
  };
  return (
    <button
      type="button"
      onClick={replay}
      className={className ?? "text-sm text-gray-600 hover:text-gray-900 hover:underline"}
    >
      ガイドを再表示
    </button>
  );
}
