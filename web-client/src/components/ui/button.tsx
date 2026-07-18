import { forwardRef, type ButtonHTMLAttributes } from "react";
import { cn } from "@/lib/utils";

export type ButtonVariant = "primary" | "secondary" | "danger" | "ghost";
export type ButtonSize = "sm" | "md";

// Semantic tokens (globals.css `@theme`); primary/danger map to the same blue-600/red-600 shades, so
// this is a token wiring with no visual change (#194).
const VARIANTS: Record<ButtonVariant, string> = {
  primary: "bg-primary text-white hover:bg-primary-hover",
  secondary: "bg-gray-100 text-gray-700 hover:bg-gray-200",
  danger: "bg-danger text-white hover:bg-danger-hover",
  ghost: "text-gray-700 hover:bg-gray-100",
};

const SIZES: Record<ButtonSize, string> = {
  sm: "px-3 py-1 text-sm",
  md: "px-4 py-2 text-sm",
};

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  size?: ButtonSize;
};

/**
 * Shared button primitive (#194, UX-5). Replaces the copy-pasted
 * `bg-blue-… text-white px-4 py-2 rounded-md` blocks scattered across the modals/forms with one
 * tokenized component. `cn()` (tailwind-merge) lets callers extend or override individual classes,
 * and it defaults to `type="button"` so it never accidentally submits a form.
 */
export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = "primary", size = "md", type = "button", className, ...props },
  ref,
) {
  return (
    <button
      ref={ref}
      type={type}
      className={cn(
        "inline-flex items-center justify-center rounded-md font-medium transition-colors",
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500",
        "disabled:cursor-not-allowed disabled:opacity-50",
        VARIANTS[variant],
        SIZES[size],
        className,
      )}
      {...props}
    />
  );
});
