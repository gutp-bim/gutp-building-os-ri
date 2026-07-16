/**
 * Where an authenticated user lands after login (#178/#185/#191). Every entry point — the root `/`
 * redirect, a re-visit to `/sign-in`, and the OIDC callback — must send the user to the operator
 * home so the landing screen is the same regardless of how they signed in. Kept as a single
 * constant so no entry point can drift back to a stale target (e.g. `/buildings`, which only
 * redirects on to `/resources`, giving a two-hop landing that varied by entry point).
 */
export const POST_LOGIN_PATH = "/home";
