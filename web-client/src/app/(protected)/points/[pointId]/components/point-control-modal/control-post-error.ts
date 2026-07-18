import type { ControlExecutionState } from "@/lib/infra/grpc-client/use-control-execution";

/**
 * 制御 POST（`POST /points/{id}/control`）が失敗したときの HTTP ステータスを、操作フィードバック
 * の状態へ写像する純関数（#162）。
 *
 * 403（権限不足）と 503（ゲートウェイオフライン, #186）を汎用エラーと区別することで、ステータス
 * バーが「なぜ操作できないのか」を説明できるようにする。統一エラーポリシー（#162 (a), 要 product
 * 判断）には踏み込まず、既存の success/failed/timeout と同じ粒度でフィードバックを一段細かくする
 * だけに留める。
 *
 * gRPC ストリーム（`useControlExecution`）は POST が 2xx を返した後にしか開かないため、403/503 は
 * ここ（POST の catch）でしか観測されない。
 */
export type ControlPostErrorStatus =
  | "permission_denied"
  | "gateway_offline"
  | "failed";

// `ControlExecutionState["status"]` の部分集合であることを型で担保する（外れると setDirectResult
// 呼び出し側でコンパイルエラーになる）。
export type ControlPostErrorResult = {
  status: ControlPostErrorStatus & ControlExecutionState["status"];
  message: string;
};

/** aspida(axios) のエラーから HTTP ステータスコードを取り出す（`telemetry/repository.ts` と同型）。 */
function httpStatusOf(error: unknown): number | undefined {
  const status = (error as { response?: { status?: unknown } })?.response
    ?.status;
  return typeof status === "number" ? status : undefined;
}

export function controlPostErrorResult(
  error: unknown,
  pointId: string,
): ControlPostErrorResult {
  const status = httpStatusOf(error);

  if (status === 403) {
    // 権限文字列の形式は `{resourceType}:{resourceId}:{actions}`（CLAUDE.md 認可モデル）。
    return {
      status: "permission_denied",
      message: `この操作には point:${pointId}:write 権限が必要です。`,
    };
  }

  if (status === 503) {
    return {
      status: "gateway_offline",
      message:
        "ゲートウェイが接続されていないため制御を実行できません。接続状態を確認してください。",
    };
  }

  return {
    status: "failed",
    message: "制御信号の送信に失敗しました。",
  };
}
