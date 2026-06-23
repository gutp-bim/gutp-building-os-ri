import type { BuildingOsRole } from "@/lib/auth/claims";

/**
 * One onboarding tour step (#150). Content reuses the D-1 help (#149) by `helpKey`, or is provided
 * inline (`title`/`body`) for welcome/finish steps. Steps are shown only to roles in `roles`.
 */
export interface TourStep {
  id: string;
  roles: BuildingOsRole[];
  /** Reuse a D-1 help entry's title/body by key. */
  helpKey?: string;
  title?: string;
  body?: string[];
}

/** A tour step with content resolved (from a help entry or inline) — what the UI renders. */
export interface ResolvedTourStep {
  id: string;
  title: string;
  body: string[];
}
