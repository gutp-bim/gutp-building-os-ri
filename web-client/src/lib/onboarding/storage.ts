/**
 * Persistence for the onboarding tour's skip/replay state (#150). Uses localStorage; SSR-safe (treats
 * a missing window as "completed" so the tour never flashes during server render). Versioned key so a
 * future tour revision can re-trigger.
 */
const STORAGE_KEY = "buildingos.onboarding.completed.v1";

/** Custom event dispatched to ask a mounted tour to replay (used by the replay control). */
export const REPLAY_EVENT = "buildingos:onboarding-replay";

export function isTourCompleted(): boolean {
  if (typeof window === "undefined") return true;
  try {
    return window.localStorage.getItem(STORAGE_KEY) === "1";
  } catch {
    return true;
  }
}

export function markTourCompleted(): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(STORAGE_KEY, "1");
  } catch {
    // ignore (e.g. storage disabled) — worst case the tour shows again
  }
}

export function resetTour(): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // ignore
  }
}
