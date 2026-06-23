import type { BuildingOsRole } from "@/lib/auth/claims";
import { resolveHelp } from "@/lib/help/resolve";
import type { HelpEntry } from "@/lib/help/types";
import { TOUR_STEPS } from "./content";
import type { ResolvedTourStep, TourStep } from "./types";

/**
 * Pure selection + resolution for the onboarding tour (#150). Registries/resolvers are injectable so
 * the logic is unit-testable.
 */

/** Steps visible to a role, in order. Empty for an unknown role. */
export function stepsForRole(role: BuildingOsRole | null, steps: TourStep[] = TOUR_STEPS): TourStep[] {
  if (!role) return [];
  return steps.filter((s) => s.roles.includes(role));
}

/**
 * Resolves a step's content: from its D-1 help entry when `helpKey` is set, else inline title/body,
 * else a minimal fallback (title = id). This is how the tour reuses D-1 content.
 */
export function resolveTourStep(
  step: TourStep,
  resolve: (key: string) => HelpEntry | null = resolveHelp,
): ResolvedTourStep {
  if (step.helpKey) {
    const entry = resolve(step.helpKey);
    if (entry) {
      return { id: step.id, title: entry.title, body: entry.body };
    }
  }
  return { id: step.id, title: step.title ?? step.id, body: step.body ?? [] };
}

/** Convenience: the resolved, role-filtered steps for a role. */
export function resolvedStepsForRole(
  role: BuildingOsRole | null,
  steps: TourStep[] = TOUR_STEPS,
  resolve: (key: string) => HelpEntry | null = resolveHelp,
): ResolvedTourStep[] {
  return stepsForRole(role, steps).map((s) => resolveTourStep(s, resolve));
}
