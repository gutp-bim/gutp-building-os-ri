"use client";

import { useEffect, useRef, type RefObject } from "react";

const FOCUSABLE_SELECTOR = [
  "a[href]",
  "button:not([disabled])",
  "textarea:not([disabled])",
  "input:not([disabled])",
  "select:not([disabled])",
  '[tabindex]:not([tabindex="-1"])',
].join(",");

/**
 * Dialog a11y for hand-rolled overlays (#198): initial focus, Esc-to-close, and focus restoration —
 * plus, for `modal` dialogs, a real focus trap. Apply to a container that carries `role="dialog"`
 * (give it `tabIndex={-1}` so it can receive focus when it has no focusable children).
 *
 * - On open, focus moves to the first focusable element inside the container (or the container itself).
 * - Escape calls `onClose`.
 * - On close/unmount, focus returns to whatever was focused before the dialog opened (the trigger).
 * - When `modal` (default): `Tab`/`Shift+Tab` cycle within the container, AND a document-level
 *   `focusin` guard pulls focus back in if it escapes by pointer click or programmatic focus (a
 *   keydown-only trap can't catch those — see #198 review). Such a dialog must also carry
 *   `aria-modal="true"`.
 * - When `modal: false`: no trap and no focus guard — focus may leave freely (a non-modal panel must
 *   not claim to trap; it should not set `aria-modal`). Esc + initial/restored focus still apply.
 *
 * The effect depends only on `open`/`modal`, so a caller passing an inline `onClose` does not re-run
 * it (and therefore does not re-capture the trigger or steal focus mid-interaction); `onClose` is
 * read live.
 */
export function useDialogA11y(
  containerRef: RefObject<HTMLElement | null>,
  { open, onClose, modal = true }: { open: boolean; onClose: () => void; modal?: boolean },
): void {
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    if (!open) return;
    const container = containerRef.current;
    if (!container) return;

    const previouslyFocused =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;

    const focusable = () =>
      Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
        (el) => el.getAttribute("aria-hidden") !== "true",
      );

    (focusable()[0] ?? container).focus();

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (!modal || event.key !== "Tab") return;
      const items = focusable();
      if (items.length === 0) {
        event.preventDefault();
        return;
      }
      const first = items[0];
      const last = items[items.length - 1];
      const active = document.activeElement;
      if (event.shiftKey && active === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      }
    };
    container.addEventListener("keydown", onKeyDown);

    // Keydown alone can't trap focus moved by a pointer click or a programmatic `focus()` — for a
    // modal, catch those at the document level and return focus to the dialog.
    const onFocusIn = modal
      ? (event: FocusEvent) => {
          if (event.target instanceof Node && container.contains(event.target)) return;
          (focusable()[0] ?? container).focus();
        }
      : null;
    if (onFocusIn) document.addEventListener("focusin", onFocusIn);

    return () => {
      if (onFocusIn) document.removeEventListener("focusin", onFocusIn);
      container.removeEventListener("keydown", onKeyDown);
      previouslyFocused?.focus();
    };
  }, [open, modal, containerRef]);
}
