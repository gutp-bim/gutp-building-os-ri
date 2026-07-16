import Link from "next/link";

/**
 * App-wide 404 (#190). Next.js renders its built-in English "This page could not be found" for
 * unknown routes and for `notFound()` on detail routes that lack their own boundary
 * (floors/spaces/devices/points). This gives them a Japanese recovery screen consistent with the
 * product tone, with a route back to the operator home.
 */
export default function NotFound() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center px-4 text-center">
      <h1 className="mb-4 text-3xl font-bold">ページが見つかりません</h1>
      <p className="mb-8 text-gray-600">
        お探しのページは存在しないか、移動した可能性があります。
      </p>
      <Link
        href="/home"
        className="rounded bg-blue-500 px-4 py-2 text-white transition-colors hover:bg-blue-600"
      >
        ホームに戻る
      </Link>
    </div>
  );
}
