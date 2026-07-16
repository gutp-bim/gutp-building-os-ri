import Link from "next/link";

export default function NotFound() {
  return (
    <div className="flex flex-col items-center justify-center min-h-screen">
      <h2 className="text-3xl font-bold mb-4">建物が見つかりません</h2>
      <p className="text-gray-600 mb-8">
        お探しの建物は存在しないか、アクセス権限がありません。
      </p>
      <div className="flex gap-4">
        <Link
          href="/resources"
          className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 transition-colors"
        >
          リソース一覧に戻る
        </Link>
        <Link
          href="/home"
          className="px-4 py-2 bg-gray-500 text-white rounded hover:bg-gray-600 transition-colors"
        >
          ホームに戻る
        </Link>
      </div>
    </div>
  );
}
