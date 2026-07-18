import Link from "next/link";

/**
 * A keyboard-accessible navigation card for the resource-hierarchy detail pages (#195).
 *
 * Renders a real `next/link`, so it is focusable, Enter/⌘-click aware, and announced as a link —
 * replacing the old `<div onClick>` cards that were mouse-only and invisible to assistive tech. The
 * link's accessible name is the resource `name` (via `aria-label`) so a screen reader announces the
 * target concisely instead of reading the long URN id aloud.
 */
export function ResourceNavCard({
  href,
  name,
  subtitle,
  id,
  testId,
}: {
  href: string;
  name: string;
  subtitle?: string;
  id: string;
  testId?: string;
}) {
  return (
    <Link
      href={href}
      aria-label={name}
      data-testid={testId}
      className="block rounded-lg border border-gray-200 bg-white p-6 shadow-sm transition-shadow hover:shadow-md focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
    >
      <div className="mb-2 text-lg font-semibold text-gray-900">{name}</div>
      {subtitle && <p className="text-sm text-gray-600">{subtitle}</p>}
      <p className="mt-2 truncate text-xs text-gray-600">ID: {id}</p>
    </Link>
  );
}
