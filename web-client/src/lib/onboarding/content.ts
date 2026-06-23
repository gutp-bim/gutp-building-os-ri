import type { TourStep } from "./types";

const ALL_ROLES = ["admin", "operator", "viewer"] as const;

/**
 * Onboarding tour steps (#150). Admin-only steps cover the platform screens (reusing D-1 help, #149);
 * everyone gets the welcome/operator/finish steps. Extend by adding steps — selection and rendering
 * are driven by this array.
 */
export const TOUR_STEPS: TourStep[] = [
  {
    id: "welcome",
    roles: [...ALL_ROLES],
    title: "Building OS へようこそ",
    body: [
      "この短いガイドで主要な画面を紹介します。",
      "スキップしても、各画面の「?」ボタンやヘッダーの「ガイドを再表示」からいつでも確認できます。",
    ],
  },
  {
    id: "operator",
    roles: [...ALL_ROLES],
    title: "運用ワークスペース",
    body: [
      "「運用（建物）」では、建物 → フロア → 空間 → 機器 → ポイントの階層をたどって状態を確認できます。",
    ],
  },
  // Admin-only: platform screens — content reused from D-1 help (#149).
  { id: "platform-status", roles: ["admin"], helpKey: "platform.status" },
  { id: "platform-config", roles: ["admin"], helpKey: "platform.config" },
  { id: "platform-settings", roles: ["admin"], helpKey: "platform.settings" },
  {
    id: "finish",
    roles: [...ALL_ROLES],
    title: "準備完了",
    body: ["以上です。各画面の「?」から、用語集付きの詳しい解説をいつでも開けます。"],
  },
];
