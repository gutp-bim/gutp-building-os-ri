import { UserManager, WebStorageStateStore } from "oidc-client-ts";

export function createOidcUserManager(): UserManager {
  const scope = ["openid", "profile", "email", "building-os-api"].join(" ");

  return new UserManager({
    authority: process.env.NEXT_PUBLIC_KEYCLOAK_AUTHORITY ?? "",
    client_id: process.env.NEXT_PUBLIC_KEYCLOAK_CLIENT_ID ?? "",
    redirect_uri:
      typeof window !== "undefined"
        ? `${window.location.origin}/auth/oidc-callback`
        : "",
    post_logout_redirect_uri:
      typeof window !== "undefined" ? window.location.origin : "",
    scope,
    userStore:
      typeof window !== "undefined"
        ? new WebStorageStateStore({ store: localStorage })
        : undefined,
  });
}

export const OIDC_TOKEN_COOKIE = "oidc.access_token";
