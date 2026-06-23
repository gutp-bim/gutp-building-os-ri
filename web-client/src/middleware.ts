import type { NextRequest } from "next/server";
import { NextResponse } from "next/server";
import { parseAuthClaims } from "@/lib/auth/claims";
import {
  WORKSPACES,
  canAccessWorkspace,
  defaultWorkspace,
} from "@/lib/auth/workspaces";
import { workspaceForPath } from "@/lib/nav/active";

// Cookie name mirrors OIDC_TOKEN_COOKIE in lib/auth/oidc-config; duplicated here because that
// module pulls in oidc-client-ts, which cannot run in the edge middleware runtime.
const OIDC_TOKEN_COOKIE = "oidc.access_token";

export function middleware(request: NextRequest) {
  const token = request.cookies.get(OIDC_TOKEN_COOKIE)?.value;
  const isAuthenticated = Boolean(token);
  const { pathname } = request.nextUrl;
  const isAuthPage =
    pathname.startsWith("/sign-in") || pathname.startsWith("/auth/");

  if (!isAuthenticated && !isAuthPage) {
    return NextResponse.redirect(new URL("/sign-in", request.url));
  }

  if (isAuthenticated && isAuthPage) {
    return NextResponse.redirect(new URL("/", request.url));
  }

  // Workspace guard: keep users out of workspace sections their role can't enter. This is a UX
  // affordance (hide what you can't use), NOT the authorization boundary — the API re-checks every
  // request. Signature is intentionally not verified here.
  //
  // We only redirect when the role has a reachable fallback workspace. An unknown/missing role
  // (no fallback) is let through to the page so the API can return the real error — redirecting it
  // would bounce to `/` → `/buildings` and, since that target is itself a workspace, risk a loop.
  if (isAuthenticated) {
    const { role } = parseAuthClaims(token);
    const targetWorkspace = workspaceForPath(pathname);
    const fallback = defaultWorkspace(role);
    if (
      targetWorkspace &&
      fallback &&
      !canAccessWorkspace(role, targetWorkspace)
    ) {
      const dest = WORKSPACES[fallback].defaultPath;
      if (dest !== pathname) {
        return NextResponse.redirect(new URL(dest, request.url));
      }
    }
  }

  return NextResponse.next();
}

export const config = {
  matcher: [
    "/((?!.swa|api|_next/static|_next/image|favicon.ico|grpc-test).*)",
  ],
};
