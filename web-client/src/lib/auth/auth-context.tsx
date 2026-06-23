"use client";

import { OidcAuthProvider, useOidcAuth } from "./oidc-auth-provider";

export function AuthProvider({ children }: { children: React.ReactNode }) {
  return <OidcAuthProvider>{children}</OidcAuthProvider>;
}

export function UnifiedAuthProvider({
  children,
}: {
  children: React.ReactNode;
}) {
  return <AuthProvider>{children}</AuthProvider>;
}

export function useAuthProvider() {
  const oidcCtx = useOidcAuth();
  return {
    isAuthenticated: oidcCtx.isAuthenticated,
    signIn: async (_u: string, _p: string) => {},
    signInWithOidc: oidcCtx.signInWithOidc,
    signOut: oidcCtx.signOut,
  };
}

export const useAuth = () => {
  return useAuthProvider();
};
