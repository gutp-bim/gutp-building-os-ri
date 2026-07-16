"use client";

import { useEffect } from "react";
import Link from "next/link";

/**
 * App-wide error boundary (#190). Catches render errors thrown below the root layout that no route
 * boundary handles, replacing Next.js's built-in English fallback with a Japanese recovery screen
 * (retry + a route back to the operator home).
 */
export default function Error({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    console.error(error);
  }, [error]);

  return (
    <div className="flex min-h-screen flex-col items-center justify-center px-4 text-center">
      <h1 className="mb-4 text-3xl font-bold">エラーが発生しました</h1>
      <p className="mb-8 text-gray-600">
        申し訳ありません。予期せぬエラーが発生しました。
      </p>
      <div className="flex gap-4">
        <button
          onClick={reset}
          className="rounded bg-blue-500 px-4 py-2 text-white transition-colors hover:bg-blue-600"
        >
          もう一度試す
        </button>
        <Link
          href="/home"
          className="rounded bg-gray-500 px-4 py-2 text-white transition-colors hover:bg-gray-600"
        >
          ホームに戻る
        </Link>
      </div>
    </div>
  );
}
