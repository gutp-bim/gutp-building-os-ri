"use client";

import { useEffect } from "react";
import Link from "next/link";

/**
 * Root error boundary (#190). Unlike `error.tsx`, this replaces the root layout itself, so it must
 * render its own `<html>`/`<body>`. It only fires when the root layout or a provider throws; a plain
 * Japanese fallback with a route back to the operator home.
 */
export default function GlobalError({
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
    <html lang="ja">
      <body>
        <div className="flex min-h-screen flex-col items-center justify-center px-4 text-center">
          <h1 className="mb-4 text-3xl font-bold">エラーが発生しました</h1>
          <p className="mb-8 text-gray-600">
            申し訳ありません。アプリの読み込み中に問題が発生しました。
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
      </body>
    </html>
  );
}
