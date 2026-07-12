"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { createOidcUserManager } from "@/lib/auth/oidc-config";

// Module-level flag: survives StrictMode's simulated unmount/remount cycle.
let _processing = false;

export default function OidcCallbackPage() {
  const router = useRouter();

  useEffect(() => {
    if (_processing) return;
    _processing = true;

    const manager = createOidcUserManager();
    manager
      .signinRedirectCallback()
      .then(() => {
        router.replace("/buildings");
      })
      .catch((err) => {
        console.error("OIDC callback error:", err);
        _processing = false;
        router.replace("/sign-in");
      });
  }, [router]);

  return (
    <div className="min-h-screen flex items-center justify-center">
      <p className="text-gray-600">認証処理中...</p>
    </div>
  );
}
