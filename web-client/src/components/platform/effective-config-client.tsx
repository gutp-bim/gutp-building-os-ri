"use client";

import { useEffect, useRef, useState } from "react";
import { fetchEffectiveConfig } from "@/lib/system-config/fetch-config";
import type { EffectiveConfig } from "@/lib/system-config/types";
import { HelpButton } from "@/components/help/help-button";
import { EffectiveConfigView } from "./effective-config-view";

/** Loads `GET /api/system/config` and renders the read-only {@link EffectiveConfigView} (#147). */
export function EffectiveConfigClient() {
  const [config, setConfig] = useState<EffectiveConfig | null>(null);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    fetchEffectiveConfig(controller.signal)
      .then((c) => {
        if (mounted.current) setConfig(c);
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError" && mounted.current) setError(e.message);
      });
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, []);

  return (
    <div className="container mx-auto px-4 py-8">
      <div className="mb-1 flex items-center gap-2">
        <h1 className="text-2xl font-bold">設定（実効値）</h1>
        <HelpButton helpKey="platform.config" />
      </div>
      <p className="mb-4 text-sm text-gray-500">
        読み取り専用です。設定の source of truth は IaC / ArgoCD で、シークレットは値を表示しません。
      </p>
      {error ? (
        <p className="text-red-600" data-testid="config-error">
          設定の取得に失敗しました: {error}
        </p>
      ) : config === null ? (
        <p className="text-gray-500">読み込み中…</p>
      ) : (
        <EffectiveConfigView entries={config.entries} />
      )}
    </div>
  );
}
