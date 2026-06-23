import { AppShell } from "@/components/shell/app-shell";

/**
 * Wraps every authenticated page in the global shell (header + workspace switcher + sidebar).
 * Existing pages keep their current routes — the shell is overlaid non-destructively.
 */
export default function ProtectedLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return <AppShell>{children}</AppShell>;
}
