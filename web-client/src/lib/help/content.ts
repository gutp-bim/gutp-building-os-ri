import type { GlossaryTerm, HelpEntry } from "./types";

/**
 * Seed help content (#149). Extend by adding entries/terms — the resolution logic and UI are driven
 * by these arrays. Keep definitions decisive and self-contained (they double as LLM prompt material).
 */
export const GLOSSARY: GlossaryTerm[] = [
  {
    term: "ポイント",
    reading: "ぽいんと",
    definition:
      "機器（Equipment）に属する計測点・制御点。SBCO オントロジーの sbco:PointExt に対応し、テレメトリの最小単位です。",
    category: "ontology",
  },
  {
    term: "機器",
    reading: "きき",
    definition:
      "空調・電力計・環境センサーなどの設備。SBCO の sbco:EquipmentExt に対応し、複数のポイントを持ちます。",
    category: "ontology",
  },
  {
    term: "メッセージレート",
    definition:
      "コネクターが直近1分間に処理したテレメトリ件数の合計（毎秒）。データ取り込みの活性度を表します。",
    category: "metric",
  },
  {
    term: "制御リクエスト数",
    definition:
      "直近5分間に発行された機器制御コマンドの件数。書き込み（制御）操作の量を表します。",
    category: "metric",
  },
  {
    term: "鮮度切れ閾値",
    definition:
      "テレメトリを「鮮度切れ（stale）」とみなすまでの秒数。アプリ設定で変更でき、表示や警告の判定に使われます。",
    category: "setting",
  },
];

export const HELP_ENTRIES: HelpEntry[] = [
  {
    key: "platform.status",
    title: "システム稼働状態",
    body: [
      "各サービスの up/down と主要 KPI を1画面に集約して表示します。",
      "サービスの up/down は /health のファンアウトで判定するため、Prometheus を起動していなくても確認できます。KPI は Prometheus 未配線時は空欄になります。",
    ],
    relatedTerms: ["メッセージレート", "制御リクエスト数"],
  },
  {
    key: "platform.config",
    title: "設定（実効値）",
    body: [
      "API サーバーの実効設定を読み取り専用で表示します。設定の source of truth は IaC / ArgoCD で、ここからは編集できません。",
      "シークレットは値を表示せず、設定済み / 未設定 のみ表示します。",
    ],
    relatedTerms: [],
  },
  {
    key: "platform.settings",
    title: "アプリ設定",
    body: [
      "フィーチャーフラグや閾値など、GitOps と衝突しないアプリ設定のみを編集できます。",
      "許可リストに登録されたキーのみ編集でき、値は型検証されます。「既定値に戻す」で上書きを取り消せます。",
    ],
    relatedTerms: ["鮮度切れ閾値"],
  },
  {
    key: "admin.gateways",
    title: "ゲートウェイ",
    body: [
      "binding / 接続設定と pointlist 同期状態の観測画面です。binding や twin 上のポイント登録は GitOps / デジタルツインが正本のため、この画面自体に作成・登録操作はありません。",
      "新しいゲートウェイをオンボードする手順(twin へのポイント登録・制御 binding・ingress/egress ポート・pointlist 同期)は docs/gateway-onboarding-checklist.md に集約されています。",
    ],
    relatedTerms: [],
  },
];
