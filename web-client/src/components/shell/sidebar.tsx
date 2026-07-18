"use client";

import { XMarkIcon } from "@heroicons/react/24/outline";
import Link from "next/link";
import { useRef } from "react";
import { cn } from "@/lib/utils";
import { useDialogA11y } from "@/lib/a11y/use-dialog-a11y";
import type { Workspace } from "@/lib/auth/workspaces";
import { isNavItemActive } from "@/lib/nav/active";
import { visibleNavItems } from "@/lib/nav/nav-config";

type SidebarProps = {
  workspace: Workspace | null;
  permissions: string[];
  pathname: string;
  /** Whether the off-canvas mobile drawer is open (on `md`+ the sidebar is always visible). */
  open?: boolean;
  onClose?: () => void;
};

type NavItems = ReturnType<typeof visibleNavItems>;

/** The workspace nav links, shared by the desktop column and the mobile drawer. */
function NavLinks({
  items,
  pathname,
  onNavigate,
}: {
  items: NavItems;
  pathname: string;
  onNavigate?: () => void;
}) {
  return (
    <>
      {items.map((item) => {
        const active = isNavItemActive(pathname, item);
        return (
          <Link
            key={item.href}
            href={item.href}
            onClick={onNavigate}
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
    </>
  );
}

/**
 * The mobile off-canvas drawer (below `md`). It is a **modal dialog**: mounted only while open (so
 * nothing behind the scrim stays in the tab order when closed — a `-translate-x-full` element is
 * merely offscreen, still focusable, #199 review), and it binds {@link useDialogA11y} (#198) to move
 * focus in on open, trap `Tab`/`Shift+Tab`, close on `Escape`, and restore focus to the hamburger
 * trigger on close.
 */
function MobileNavDrawer({
  items,
  pathname,
  onClose,
}: {
  items: NavItems;
  pathname: string;
  onClose: () => void;
}) {
  const drawerRef = useRef<HTMLElement>(null);
  useDialogA11y(drawerRef, { open: true, onClose });

  return (
    <div className="md:hidden">
      <button
        type="button"
        aria-label="メニューを閉じる"
        data-testid="sidebar-scrim"
        className="fixed inset-0 z-30 bg-black/30"
        onClick={onClose}
      />
      <nav
        ref={drawerRef}
        id="app-sidebar"
        role="dialog"
        aria-modal="true"
        aria-label="メインナビゲーション"
        data-testid="sidebar-drawer"
        tabIndex={-1}
        className="fixed inset-y-0 left-0 z-40 flex w-56 flex-col gap-1 border-r border-gray-200 bg-gray-50 p-3"
      >
        <div className="mb-1 flex justify-end">
          <button
            type="button"
            onClick={onClose}
            aria-label="閉じる"
            className="rounded p-1 text-gray-600 hover:bg-gray-100"
          >
            <XMarkIcon className="h-5 w-5" />
          </button>
        </div>
        <NavLinks items={items} pathname={pathname} onNavigate={onClose} />
      </nav>
    </div>
  );
}

/**
 * Workspace-scoped navigation. Items the user lacks permission for are simply not rendered.
 *
 * On `md`+ it is a static column; below `md` it becomes an off-canvas drawer (#199) toggled from the
 * header hamburger. The two are separate elements — the desktop column keeps normal navigation
 * semantics and is always focusable, while the mobile drawer is a modal dialog that only exists in
 * the DOM while open (see {@link MobileNavDrawer}), so a keyboard user can never Tab into a hidden
 * offscreen nav.
 */
export function Sidebar({ workspace, permissions, pathname, open = false, onClose }: SidebarProps) {
  const items = workspace ? visibleNavItems(workspace, permissions) : [];

  return (
    <>
      <nav
        aria-label="メインナビゲーション"
        data-testid="sidebar-desktop"
        className="hidden w-56 shrink-0 flex-col gap-1 border-r border-gray-200 bg-gray-50 p-3 md:flex"
      >
        <NavLinks items={items} pathname={pathname} />
      </nav>
      {open && onClose && (
        <MobileNavDrawer items={items} pathname={pathname} onClose={onClose} />
      )}
    </>
  );
}
