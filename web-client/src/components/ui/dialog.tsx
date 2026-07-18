"use client";

import { useDialogA11y } from "@/lib/a11y/use-dialog-a11y";
import { cn } from "@/lib/utils";
import { useRef, type ReactNode } from "react";

export type DialogPlacement = "center" | "drawer-right";

/**
 * Shared modal dialog primitive (#194, UX-5). One focus-trapped overlay base for the app's
 * hand-rolled modals, replacing the per-overlay `fixed inset-0` + scrim + `role="dialog"` + a11y
 * boilerplate. It renders a scrim and an a11y-wired panel — focus moves in on open, `Tab`/`Shift+Tab`
 * are trapped, `Escape` closes, and focus restores to the trigger (via {@link useDialogA11y}, #198).
 *
 * Non-modal floating helpers (e.g. the assistant panel) deliberately do NOT use this — they bind
 * `useDialogA11y({ modal: false })` directly so focus can leave freely and they claim no scrim.
 */
export function Dialog({
  open = true,
  onClose,
  placement = "center",
  dismissable = true,
  labelledBy,
  label,
  scrimLabel = "閉じる",
  panelClassName,
  testId,
  children,
}: {
  open?: boolean;
  onClose: () => void;
  placement?: DialogPlacement;
  /** Whether clicking the scrim closes the dialog. `false` for flows needing an explicit action (e.g. a tour). */
  dismissable?: boolean;
  labelledBy?: string;
  label?: string;
  /**
   * Accessible name for the scrim (click-to-close) button. Override when the panel already carries a
   * generic "閉じる" control so the two do not collide under `getByLabelText`.
   */
  scrimLabel?: string;
  /** Classes for the dialog panel itself (size, background, padding). */
  panelClassName?: string;
  testId?: string;
  children: ReactNode;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  useDialogA11y(panelRef, { open, onClose });

  if (!open) return null;

  const panel = (
    <div
      ref={panelRef}
      role="dialog"
      aria-modal="true"
      aria-labelledby={labelledBy}
      aria-label={label}
      tabIndex={-1}
      className={cn(placement === "center" && "relative", panelClassName)}
    >
      {children}
    </div>
  );

  if (placement === "drawer-right") {
    return (
      <div className="fixed inset-0 z-50 flex justify-end" data-testid={testId}>
        <button
          type="button"
          aria-label={scrimLabel}
          className="flex-1 bg-black/30"
          onClick={onClose}
        />
        {panel}
      </div>
    );
  }

  // center — the backdrop tint is on the container; an optional transparent overlay button handles
  // click-to-close so non-dismissable flows simply omit it.
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40"
      data-testid={testId}
    >
      {dismissable && (
        <button
          type="button"
          aria-label={scrimLabel}
          className="absolute inset-0"
          onClick={onClose}
        />
      )}
      {panel}
    </div>
  );
}
