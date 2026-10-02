"use client";

/**
 * Point 詳細の「この Point の直近イベント」（#455）。
 *
 * 健全性パネル（#457）が*いまの判定とその根拠*を見せるのに対し、こちらは**この Point で過去に何が起き、
 * 解消したか、誰が確認したか**。発生中のものは先頭に出る（新しい順）。
 *
 * 取得は自前（注入可能）で、**失敗しても何も出さない**だけにする — 履歴はこの画面の主目的ではなく、
 * 取れないことで Point 詳細を壊したくないため。
 */

import {
  fetchHealthEvents,
  KIND_LABEL,
  SEVERITY_LABEL,
  summarizeDetail,
  type EventsQuery,
  type HealthEvent,
  type HealthEventPage,
} from "@/lib/health/events";
import Link from "next/link";
import { useEffect, useState } from "react";

const RECENT = 5;

function when(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime())
    ? "—"
    : d.toLocaleString("ja-JP", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

export function PointHealthEvents({
  pointId,
  load = fetchHealthEvents,
}: {
  pointId: string;
  load?: (q: EventsQuery) => Promise<HealthEventPage>;
}) {
  const [events, setEvents] = useState<HealthEvent[] | null>(null);

  useEffect(() => {
    let active = true;
    load({ lifecycle: "", ack: "", kinds: [], subjectType: "point", subjectId: pointId, limit: RECENT, offset: 0 })
      .then((p) => active && setEvents(p.items))
      .catch(() => active && setEvents(null));
    return () => {
      active = false;
    };
  }, [pointId, load]);

  if (events === null) return null;

  return (
    <section className="mt-4 rounded border border-gray-200 p-3" data-testid="point-health-events">
      <div className="mb-2 flex items-center justify-between">
        <h3 className="text-sm font-semibold">この Point の直近イベント</h3>
        <Link
          href={`/health?view=events&lifecycle=all`}
          className="text-xs text-blue-700 hover:underline"
        >
          すべてのイベント
        </Link>
      </div>
      {events.length === 0 ? (
        <p className="text-xs text-gray-600" data-testid="point-events-empty">
          記録されたイベントはありません。
        </p>
      ) : (
        <ul className="space-y-2 text-sm">
          {events.map((e) => (
            <li key={e.id} data-testid={`point-event-${e.id}`}>
              <span className="font-medium">{KIND_LABEL[e.kind]}</span>
              <span className="ml-1 text-xs text-gray-500">（{SEVERITY_LABEL[e.severity]}）</span>
              <span className="ml-2 text-gray-700">{summarizeDetail(e)}</span>
              <div className="text-xs text-gray-500">
                {when(e.raisedAt)} →{" "}
                {e.isOpen ? <span className="text-amber-700">未解消</span> : when(e.clearedAt)}
                {e.acknowledgedAt && (
                  <span data-testid="point-event-ack"> ・ {e.acknowledgedBy ?? "確認済み"} が確認（{when(e.acknowledgedAt)}）</span>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
