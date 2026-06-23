"use client";

import { useState } from "react";
import { relatedTerms, resolveHelp } from "@/lib/help/resolve";
import { HelpDrawer } from "./help-drawer";

/**
 * A "?" button bound to a screen via {@link helpKey} (#149). Resolves the help entry + related
 * glossary terms from content-as-code and opens the {@link HelpDrawer}.
 */
export function HelpButton({ helpKey, className }: { helpKey: string; className?: string }) {
  const [open, setOpen] = useState(false);
  const entry = resolveHelp(helpKey);
  const terms = entry ? relatedTerms(entry) : [];

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label="ヘルプを開く"
        className={
          className ??
          "inline-flex h-6 w-6 items-center justify-center rounded-full border border-gray-300 text-sm text-gray-500 hover:bg-gray-50"
        }
      >
        ?
      </button>
      <HelpDrawer entry={entry} terms={terms} open={open} onClose={() => setOpen(false)} />
    </>
  );
}
