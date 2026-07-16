"use client";

import { useAuth } from "@/lib/auth/auth-context";
import { POST_LOGIN_PATH } from "@/lib/auth/redirects";
import { useRouter } from "next/navigation";
import { useEffect } from "react";

export default function SignInPage() {
  const { signInWithOidc, isAuthenticated } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (isAuthenticated) {
      router.push(POST_LOGIN_PATH);
    }
  }, [isAuthenticated, router]);

  return (
    <div className="min-h-screen flex items-center justify-center bg-white">
      <div className="max-w-md w-full space-y-8 p-8 bg-white rounded-lg shadow">
        <div>
          <h2 className="text-center text-3xl font-extrabold text-gray-900">
            サインイン
          </h2>
        </div>
        <div className="mt-6">
          <button
            onClick={() => signInWithOidc()}
            className="w-full flex items-center justify-center gap-3 px-4 py-2 border border-gray-300 shadow-sm text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
          >
            Keycloakでサインイン
          </button>
        </div>
      </div>
    </div>
  );
}
