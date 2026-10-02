"use client";

import { DataHealthView } from "@/components/health/data-health-view";
import { HealthEventsView } from "@/components/health/health-events-view";
import { parseAuthClaims } from "@/lib/auth/claims";
import { OIDC_TOKEN_COOKIE } from "@/lib/auth/oidc-config";
import {
  acknowledgeHealthEvent,
  fetchHealthEvents,
  parseEventsQuery,
  serializeEventsQuery,
  type EventsQuery,
} from "@/lib/health/events";
import { productionHealthLoaders } from "@/lib/health/loaders";
import {
  parseHealthQuery,
  serializeHealthQuery,
  type HealthQuery,
} from "@/lib/health/query";
import Cookies from "js-cookie";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useMemo } from "react";

const EVENT_LOADERS = {
  loadEvents: (q: EventsQuery) => fetchHealthEvents(q),
  acknowledge: (id: string) => acknowledgeHealthEvent(id),
};

/** Roles that may acknowledge an event (the server enforces it too; this only hides the button). */
const ACK_ROLES = ["admin", "operator"];

/**
 * `/health`（データ品質, #453）の結線。`?view=events` で永続ヘルスイベント（#455）のタブに切り替わる。
 *
 * **検索条件は URL が正本**なので、ここは「URL → 条件」「条件 → URL」を繋ぐだけで状態を持たない
 * （共有・ブックマーク・リロードで同じ一覧が再現できることが運用要件）。履歴には `push` ではなく
 * `replace` で書く — チップを何度か押しただけで戻るボタンが使い物にならなくなるのを避けるため。
 */
export default function HealthPageComponent() {
  const router = useRouter();
  const searchParams = useSearchParams();

  const query = useMemo(
    () => parseHealthQuery(new URLSearchParams(searchParams.toString())),
    [searchParams],
  );

  const onQueryChange = useCallback(
    (next: HealthQuery) => {
      const params = serializeHealthQuery(next).toString();
      router.replace(params === "" ? "/health" : `/health?${params}`);
    },
    [router],
  );

  const view = searchParams.get("view") === "events" ? "events" : "state";
  const eventsQuery = useMemo(
    () => parseEventsQuery(new URLSearchParams(searchParams.toString())),
    [searchParams],
  );
  const onEventsQueryChange = useCallback(
    (next: EventsQuery) => {
      const params = serializeEventsQuery(next);
      params.set("view", "events");
      router.replace(`/health?${params.toString()}`);
    },
    [router],
  );
  const role = parseAuthClaims(Cookies.get(OIDC_TOKEN_COOKIE) ?? null).role;
  const canAcknowledge = role !== null && ACK_ROLES.includes(role);

  return (
    <div>
      <nav className="mb-4 flex gap-1 border-b border-gray-200" aria-label="データ健全性" data-testid="health-tabs">
        {[
          { id: "state", href: "/health", label: "現在の状態" },
          { id: "events", href: "/health?view=events", label: "イベント" },
        ].map((t) => (
          <Link
            key={t.id}
            href={t.href}
            aria-current={view === t.id ? "page" : undefined}
            data-testid={`health-tab-${t.id}`}
            className={`-mb-px border-b-2 px-4 py-2 text-sm ${
              view === t.id
                ? "border-blue-600 font-medium text-blue-700"
                : "border-transparent text-gray-600 hover:text-gray-900"
            }`}
          >
            {t.label}
          </Link>
        ))}
      </nav>
      {view === "events" ? (
        <HealthEventsView
          loaders={EVENT_LOADERS}
          query={eventsQuery}
          onQueryChange={onEventsQueryChange}
          canAcknowledge={canAcknowledge}
        />
      ) : (
        <DataHealthView
          loaders={productionHealthLoaders}
          query={query}
          onQueryChange={onQueryChange}
        />
      )}
    </div>
  );
}
