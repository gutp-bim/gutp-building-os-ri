"use client";

import { useState } from "react";
import {
  applyTwinImport,
  canApplyImport,
  controlSchemaIssueReasonLabel,
  orphanReasonLabel,
  previewSummary,
  previewTwinImport,
  runReadOnlySparql,
  type SparqlQueryResult,
  type TwinImportMode,
  type TwinImportPreview,
} from "@/lib/admin/twin-admin";

/**
 * デジタルツイン管理（#322）: 読み取り専用 SPARQL コンソール + RDF 取込（プレビュー→検証→適用）。
 * 取込は gateway_id 一意性と階層未接続（#291）をプレビューで検証し、違反があれば適用ボタンを無効化する。
 * 階層未接続のみ、明示的なチェックボックス操作で上書き適用できる（監査に記録される）。
 */
export function TwinAdminPageClient() {
  // ── SPARQL console ──
  const [query, setQuery] = useState("SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 20");
  const [result, setResult] = useState<SparqlQueryResult | null>(null);
  const [queryError, setQueryError] = useState<string | null>(null);
  const [querying, setQuerying] = useState(false);

  const runQuery = () => {
    setQuerying(true);
    setQueryError(null);
    runReadOnlySparql(query)
      .then(setResult)
      .catch((e: Error) => setQueryError(e.message))
      .finally(() => setQuerying(false));
  };

  // ── Import ──
  const [turtle, setTurtle] = useState("");
  const [mode, setMode] = useState<TwinImportMode>("append");
  const [preview, setPreview] = useState<TwinImportPreview | null>(null);
  const [allowOrphans, setAllowOrphans] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);
  const [importNotice, setImportNotice] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);

  const onFile = (file: File | undefined) => {
    if (!file) return;
    file.text().then((t) => {
      setTurtle(t);
      setPreview(null);
      setAllowOrphans(false);
    });
  };

  const doPreview = () => {
    setImporting(true);
    setImportError(null);
    setImportNotice(null);
    previewTwinImport(turtle, mode)
      .then(setPreview)
      .catch((e: Error) => setImportError(e.message))
      .finally(() => setImporting(false));
  };

  const doApply = () => {
    setImporting(true);
    setImportError(null);
    setImportNotice(null);
    applyTwinImport(turtle, mode, allowOrphans)
      .then((p) => {
        setPreview(p);
        setImportNotice(`適用しました（${mode === "replace" ? "全置換" : "追記"}）`);
      })
      .catch((e: Error) => setImportError(e.message))
      .finally(() => setImporting(false));
  };

  return (
    <div className="container mx-auto space-y-8 px-4 py-8">
      <h1 className="text-2xl font-bold">デジタルツイン管理</h1>

      {/* SPARQL console */}
      <section data-testid="sparql-console">
        <h2 className="mb-2 text-lg font-semibold">SPARQL コンソール（読み取り専用 SELECT / ASK）</h2>
        <textarea
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          rows={5}
          className="w-full rounded border border-gray-300 p-2 font-mono text-sm"
          data-testid="sparql-input"
        />
        <div className="mt-2 flex items-center gap-3">
          <button
            type="button"
            disabled={querying}
            onClick={runQuery}
            className="rounded bg-blue-600 px-3 py-1 text-sm text-white hover:bg-blue-700 disabled:opacity-50"
            data-testid="run-query"
          >
            実行
          </button>
          {result && (
            <span className="text-xs text-gray-600" data-testid="query-meta">
              {result.rowCount} 行 / {result.elapsedMs} ms{result.truncated ? "（上限で切り詰め）" : ""}
            </span>
          )}
        </div>
        {queryError && <p className="mt-2 text-sm text-red-600" data-testid="query-error">{queryError}</p>}
        {result && result.columns.length > 0 && (
          <div className="mt-3 overflow-x-auto">
            <table className="w-full text-left text-xs" data-testid="query-result">
              <thead>
                <tr className="border-b border-gray-200 text-gray-700">
                  {result.columns.map((col) => (
                    <th key={col} className="px-2 py-1 font-medium">{col}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {result.rows.map((row, i) => (
                  <tr key={i} className="border-b border-gray-100">
                    {result.columns.map((col) => (
                      <td key={col} className="px-2 py-1 font-mono">{row[col] ?? ""}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {/* Import */}
      <section data-testid="twin-import">
        <h2 className="mb-2 text-lg font-semibold">RDF / pointlist 取込</h2>
        <p className="mb-2 text-sm text-gray-600">
          TTL を貼り付けるかアップロードし、プレビューで件数・gateway_id 一意性・階層未接続を確認してから
          適用します。全置換は破壊的操作です。
        </p>
        <input
          type="file"
          accept=".ttl,.rdf,text/turtle"
          onChange={(e) => onFile(e.target.files?.[0])}
          className="mb-2 block text-sm"
          data-testid="ttl-file"
        />
        <textarea
          value={turtle}
          onChange={(e) => {
            setTurtle(e.target.value);
            setPreview(null);
            setAllowOrphans(false);
          }}
          rows={8}
          placeholder="@prefix sbco: <https://www.sbco.or.jp/ont/> . ..."
          className="w-full rounded border border-gray-300 p-2 font-mono text-sm"
          data-testid="ttl-input"
        />
        <div className="mt-2 flex flex-wrap items-center gap-3">
          <button
            type="button"
            disabled={importing || !turtle.trim()}
            onClick={doPreview}
            className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
            data-testid="preview-button"
          >
            プレビュー
          </button>
          <select
            value={mode}
            onChange={(e) => {
              setMode(e.target.value as TwinImportMode);
              // 階層未接続の判定は mode 依存（#291）。取り直すまで前の結果は使わせない。
              setPreview(null);
              setAllowOrphans(false);
            }}
            className="rounded border border-gray-300 px-2 py-1 text-sm"
            data-testid="import-mode"
          >
            <option value="append">追記</option>
            <option value="replace">全置換（破壊的）</option>
          </select>
          <button
            type="button"
            disabled={importing || !canApplyImport(preview, allowOrphans)}
            onClick={() => {
              if (mode === "replace" && !confirm("既存のツインを全置換します。よろしいですか？")) return;
              doApply();
            }}
            className="rounded bg-red-600 px-3 py-1 text-sm text-white hover:bg-red-700 disabled:opacity-50"
            data-testid="apply-button"
          >
            適用
          </button>
        </div>
        {preview && (
          <div className="mt-3 rounded border border-gray-200 p-3 text-sm" data-testid="preview-result">
            <p className={preview.valid ? "text-green-700" : "text-red-600"}>{previewSummary(preview)}</p>
            {preview.collisions.length > 0 && (
              <ul className="mt-1 list-disc pl-5 text-red-600">
                {preview.collisions.map((c) => (
                  <li key={c.gatewayId}>{c.gatewayId}: {c.buildingCount} 建物にまたがっています</li>
                ))}
              </ul>
            )}
            {preview.orphanCount > 0 && (
              <div data-testid="preview-orphans">
                <ul className="mt-1 list-disc pl-5 text-red-600">
                  {preview.orphans.map((o) => (
                    <li key={o.resourceId}>{o.resourceId}: {orphanReasonLabel(o.reason)}</li>
                  ))}
                </ul>
                {preview.orphans.length < preview.orphanCount && (
                  <p className="mt-1 text-xs text-gray-600">
                    ほか {preview.orphanCount - preview.orphans.length} 件（表示上限で省略）
                  </p>
                )}
                <label className="mt-2 flex items-center gap-2 text-xs text-gray-700">
                  <input
                    type="checkbox"
                    checked={allowOrphans}
                    onChange={(e) => setAllowOrphans(e.target.checked)}
                    data-testid="allow-orphans"
                  />
                  階層未接続を承知のうえで適用する（監査に記録されます）
                </label>
              </div>
            )}
            {preview.controlSchemaIssueCount > 0 && (
              // #336: observation-only — never blocks apply (fail-open matches the control path itself),
              // so no override checkbox here (unlike preview-orphans above).
              <div data-testid="preview-control-schema-issues">
                <ul className="mt-1 list-disc pl-5 text-amber-700">
                  {preview.controlSchemaIssues.map((i) => (
                    <li key={i.pointId}>{i.pointId}: {controlSchemaIssueReasonLabel(i.reason)}</li>
                  ))}
                </ul>
                {preview.controlSchemaIssues.length < preview.controlSchemaIssueCount && (
                  <p className="mt-1 text-xs text-gray-600">
                    ほか {preview.controlSchemaIssueCount - preview.controlSchemaIssues.length} 件（表示上限で省略）
                  </p>
                )}
              </div>
            )}
          </div>
        )}
        {importError && <p className="mt-2 text-sm text-red-600" data-testid="import-error">{importError}</p>}
        {importNotice && <p className="mt-2 text-sm text-green-700" data-testid="import-notice">{importNotice}</p>}
      </section>
    </div>
  );
}
