"use client";

import { isDemoMode } from "@/lib/auth/demo";

/**
 * Persistent demo-mode indicator (#161). When the build is the demo profile, the Web Client
 * auto-logs-in without Keycloak — this banner makes that explicit so nobody mistakes the demo's
 * skipped auth for real authentication. Renders nothing in every non-demo build.
 */
export function DemoModeBanner() {
  if (!isDemoMode()) return null;
  return (
    <div
      data-testid="demo-mode-banner"
      role="status"
      className="bg-amber-100 px-3 py-1 text-center text-xs text-amber-900"
    >
      デモモード：認証フローをスキップして自動ログインしています（本番では実際の
      Keycloak ログインが必要です）
    </div>
  );
}
