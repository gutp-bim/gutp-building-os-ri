"use client";

import { DataHealthView } from "@/components/health/data-health-view";
import { productionHealthLoaders } from "@/lib/health/loaders";
import {
  parseHealthQuery,
  serializeHealthQuery,
  type HealthQuery,
} from "@/lib/health/query";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useMemo } from "react";

/**
 * `/health`（データ品質, #453）の結線。
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

  return (
    <DataHealthView
      loaders={productionHealthLoaders}
      query={query}
      onQueryChange={onQueryChange}
    />
  );
}
