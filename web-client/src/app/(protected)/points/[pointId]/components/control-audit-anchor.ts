/**
 * Anchor for the 制御履歴 section on the point-detail page (#162).
 *
 * Shared so the result bar can link to the history without the two components guessing at each
 * other's markup — the checklist's last leg is "結果 → 当該ポイントの監査履歴への導線", and the
 * link is only useful if it lands on the section that actually got refreshed.
 */
export const CONTROL_AUDIT_ANCHOR_ID = "control-audit-history";
