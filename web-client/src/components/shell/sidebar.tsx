"use client";

import { XMarkIcon } from "@heroicons/react/24/outline";
import Link from "next/link";
import { useEffect } from "react";
import { cn } from "@/lib/utils";
import type { Workspace } from "@/lib/auth/workspaces";
import { isNavItemActive } from "@/lib/nav/active";
import { visibleNavItems } from "@/lib/nav/nav-config";

type SidebarProps = {
  workspace: Workspace | null;
  permissions: string[];
  pathname: string;
  /** Whether the off-canvas drawer is open (mobile only; on `md`+ the sidebar is always visible). */
  open?: boolean;
  onClose?: () => void;
};

/**
 * Workspace-scoped navigation. Items the user lacks permission for are simply not rendered.
 *
 * On `md`+ it is a static column; below `md` it becomes an off-canvas drawer (#199): hidden by
 * default, slid in when {@link SidebarProps.open}, with a scrim, a close button, Escape-to-close, and
 * link taps that dismiss it. `md:` classes keep the desktop layout identical.
 */
export function Sidebar({ workspace, permissions, pathname, open = false, onClose }: SidebarProps) {
  const items = workspace ? visibleNavItems(workspace, permissions) : [];

  // Escape closes the mobile drawer.
  useEffect(() => {
    if (!open || !onClose) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [open, onClose]);

  return (
    <>
      {open && onClose && (
        <button
          type="button"
          aria-label="メニューを閉じる"
          data-testid="sidebar-scrim"
          className="fixed inset-0 z-30 bg-black/30 md:hidden"
          onClick={onClose}
        />
      )}
      <nav
        id="app-sidebar"
        aria-label="メインナビゲーション"
        data-testid="sidebar"
        className={cn(
          "fixed inset-y-0 left-0 z-40 flex w-56 shrink-0 flex-col gap-1 border-r border-gray-200 bg-gray-50 p-3 transition-transform",
          "md:static md:z-auto md:translate-x-0 md:transition-none",
          open ? "translate-x-0" : "-translate-x-full",
        )}
      >
        {onClose && (
          <div className="mb-1 flex justify-end md:hidden">
            <button
              type="button"
              onClick={onClose}
              aria-label="閉じる"
              className="rounded p-1 text-gray-600 hover:bg-gray-100"
            >
              <XMarkIcon className="h-5 w-5" />
            </button>
          </div>
        )}
        {items.map((item) => {
          const active = isNavItemActive(pathname, item);
          return (
            <Link
              key={item.href}
              href={item.href}
              onClick={onClose}
              aria-current={active ? "page" : undefined}
              className={cn(
                "rounded-md px-3 py-2 text-sm font-medium transition-colors",
                active
                  ? "bg-blue-100 text-blue-800"
                  : "text-gray-700 hover:bg-gray-100 hover:text-gray-900",
              )}
            >
              {item.label}
            </Link>
          );
        })}
      </nav>
    </>
  );
}
