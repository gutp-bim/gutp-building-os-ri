"use client";

import Cookies from "js-cookie";
import { useRouter } from "next/navigation";
import type { User, UserManager } from "oidc-client-ts";
import { createContext, useContext, useEffect, useRef, useState } from "react";
import { type AuthClaims, parseAuthClaims } from "./claims";
import { buildDemoAccessToken, DEMO_USER_PROFILE, isDemoMode } from "./demo";
import { createOidcUserManager, OIDC_TOKEN_COOKIE } from "./oidc-config";

/** A synthetic oidc-client-ts User for demo mode (#161). Only the fields the provider reads are set. */
function makeDemoUser(): User {
  return {
    access_token: buildDemoAccessToken(),
    expired: false,
    profile: { ...DEMO_USER_PROFILE },
  } as unknown as User;
}

type OidcAuthContextType = {
  isAuthenticated: boolean;
  /** Role + permissions decoded from the access token. UI gating only — the API is the boundary. */
  claims: AuthClaims;
  /** Human-readable name for the signed-in user, or null when unavailable. */
  displayName: string | null;
  signInWithOidc: () => Promise<void>;
  signOut: () => void;
};

const OidcAuthContext = createContext<OidcAuthContextType | null>(null);

export function OidcAuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const managerRef = useRef<UserManager | null>(null);
  const router = useRouter();

  useEffect(() => {
    // Demo mode (#161): skip the real Keycloak flow entirely and auto-log-in as a demo admin. The
    // demo API runs DISABLE_AUTH, so the synthetic token is never validated server-side. Strictly
    // gated on the build-time NEXT_PUBLIC_DEMO_MODE flag (false in every real build).
    if (isDemoMode()) {
      const demoUser = makeDemoUser();
      Cookies.set(OIDC_TOKEN_COOKIE, demoUser.access_token, {
        sameSite: "Lax",
      });
      setUser(demoUser);
      setIsLoading(false);
      return;
    }

    const manager = createOidcUserManager();
    managerRef.current = manager;

    const storeToken = (u: User) => {
      const expiresIn = u.expires_in ?? 3600;
      Cookies.set(OIDC_TOKEN_COOKIE, u.access_token, {
        expires: expiresIn / (24 * 60 * 60),
        sameSite: "Lax",
      });
    };

    manager.getUser().then((u) => {
      if (u && !u.expired) {
        setUser(u);
        storeToken(u);
      }
      setIsLoading(false);
    });

    const onUserLoaded = (u: User) => {
      setUser(u);
      storeToken(u);
      router.push("/buildings");
    };
    const onUserUnloaded = () => {
      setUser(null);
      Cookies.remove(OIDC_TOKEN_COOKIE);
    };

    manager.events.addUserLoaded(onUserLoaded);
    manager.events.addUserUnloaded(onUserUnloaded);
    manager.events.addSilentRenewError((err) => {
      console.error("OIDC silent renew error:", err);
    });

    return () => {
      manager.events.removeUserLoaded(onUserLoaded);
      manager.events.removeUserUnloaded(onUserUnloaded);
    };
  }, [router]);

  const signInWithOidc = async () => {
    await managerRef.current?.signinRedirect();
  };

  const signOut = () => {
    Cookies.remove(OIDC_TOKEN_COOKIE);
    managerRef.current?.signoutRedirect();
  };

  if (isLoading) {
    return (
      <div className="min-h-screen flex items-center justify-center">
        <p className="text-gray-600">認証を初期化中...</p>
      </div>
    );
  }

  const isAuthenticated = !!user && !user.expired;
  const claims = parseAuthClaims(isAuthenticated ? user.access_token : null);
  const profile = user?.profile;
  const displayName =
    (profile?.name as string | undefined) ??
    (profile?.preferred_username as string | undefined) ??
    (profile?.email as string | undefined) ??
    null;

  return (
    <OidcAuthContext.Provider
      value={{
        isAuthenticated,
        claims,
        displayName,
        signInWithOidc,
        signOut,
      }}
    >
      {children}
    </OidcAuthContext.Provider>
  );
}

export const useOidcAuth = () => {
  const ctx = useContext(OidcAuthContext);
  if (!ctx) throw new Error("useOidcAuth must be used within OidcAuthProvider");
  return ctx;
};
