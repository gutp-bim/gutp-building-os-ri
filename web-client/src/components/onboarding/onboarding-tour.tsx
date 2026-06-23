"use client";

import { useEffect, useMemo, useState } from "react";
import type { BuildingOsRole } from "@/lib/auth/claims";
import { resolvedStepsForRole } from "@/lib/onboarding/select";
import { REPLAY_EVENT, isTourCompleted, markTourCompleted } from "@/lib/onboarding/storage";
import { TourStepView } from "./tour-step-view";

/**
 * First-login onboarding tour (#150). Shows role-filtered steps once (persisted via localStorage),
 * and re-opens when a {@link REPLAY_EVENT} is dispatched (the "ガイドを再表示" control). Mounted in the
 * app shell. Renders nothing when the role has no steps or the tour is already completed.
 */
export function OnboardingTour({ role }: { role: BuildingOsRole | null }) {
  const steps = useMemo(() => resolvedStepsForRole(role), [role]);
  const [index, setIndex] = useState(0);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (steps.length > 0 && !isTourCompleted()) {
      setIndex(0);
      setOpen(true);
    }
  }, [steps.length]);

  useEffect(() => {
    const replay = () => {
      if (steps.length > 0) {
        setIndex(0);
        setOpen(true);
      }
    };
    window.addEventListener(REPLAY_EVENT, replay);
    return () => window.removeEventListener(REPLAY_EVENT, replay);
  }, [steps.length]);

  if (!open || steps.length === 0) return null;

  const finish = () => {
    markTourCompleted();
    setOpen(false);
  };

  return (
    <TourStepView
      step={steps[index]}
      index={index}
      total={steps.length}
      onSkip={finish}
      onBack={() => setIndex((i) => Math.max(0, i - 1))}
      onNext={() => {
        if (index >= steps.length - 1) {
          finish();
        } else {
          setIndex((i) => i + 1);
        }
      }}
    />
  );
}
