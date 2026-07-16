"use client";

import { OperatorHome } from "@/components/home/operator-home";
import { useOidcAuth } from "@/lib/auth/oidc-auth-provider";
import { productionHomeLoaders } from "@/lib/home/loaders";

/**
 * Client wiring for the operator home (#158): production loaders + the current user's admin flag
 * (the gateway panel is admin-only). The route itself is workspace-neutral — any authenticated
 * operator lands here; admins get the extra panel.
 */
export default function HomePageComponent() {
  const { claims } = useOidcAuth();
  const isAdmin = claims.role === "admin";
  return <OperatorHome loaders={productionHomeLoaders} isAdmin={isAdmin} />;
}
