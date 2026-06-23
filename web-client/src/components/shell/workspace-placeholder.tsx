/**
 * Framed "coming soon" page for workspace sections whose real screens land in a later issue. Keeps
 * the unified shell navigable end-to-end (workspace switching never dead-ends on a 404) while the
 * actual features are built. Remove each usage as its tracking issue ships.
 */
export function WorkspacePlaceholder({
  title,
  issue,
}: {
  title: string;
  /** Tracking issue that will replace this placeholder, e.g. "#143". */
  issue: string;
}) {
  return (
    <div className="container mx-auto px-4 py-8">
      <h1 className="mb-2 text-2xl font-bold">{title}</h1>
      <p className="text-gray-600">
        この画面は準備中です（{issue} で実装予定）。
      </p>
    </div>
  );
}
