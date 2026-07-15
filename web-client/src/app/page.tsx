import { redirect } from "next/navigation";

/**
 * Post-login landing (#178). Every authenticated role lands on the operator home `/home` — the
 * task-oriented「何が届いていないか」entry point (freshness summary + attention list; admins also see
 * the gateway panel). Previously admins went to `/buildings` and everyone else to `/my-resources`;
 * the re-evaluation (§3.2 / §5 P0-2) asked for the operator home to be the default so users actually
 * reach it. To send a specific role elsewhere later (e.g. admin → `/platform/status`), branch here on
 * `parseAuthClaims(cookie).role`.
 */
export default function Home() {
  redirect("/home");
}
