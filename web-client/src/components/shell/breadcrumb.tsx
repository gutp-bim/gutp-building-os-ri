import Link from "next/link";
import { breadcrumbForPath } from "@/lib/nav/breadcrumb";

/**
 * Breadcrumb trail derived from the current path (#142). Renders nothing on unmatched paths so it
 * never shows an empty bar. The last crumb is the current page (no link, aria-current); earlier
 * crumbs link back up the workspace.
 */
export function Breadcrumb({ pathname }: { pathname: string }) {
  const crumbs = breadcrumbForPath(pathname);
  if (crumbs.length === 0) return null;

  return (
    <nav
      aria-label="パンくず"
      data-testid="breadcrumb"
      className="border-b border-gray-200 px-4 py-2 text-sm text-gray-600"
    >
      <ol className="flex flex-wrap items-center gap-1">
        {crumbs.map((c, i) => (
          <li key={`${c.label}-${i}`} className="flex items-center gap-1">
            {i > 0 ? (
              <span className="text-gray-400" aria-hidden="true">
                /
              </span>
            ) : null}
            {c.href ? (
              <Link href={c.href} className="hover:underline">
                {c.label}
              </Link>
            ) : (
              <span aria-current="page" className="font-medium text-gray-900">
                {c.label}
              </span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}
