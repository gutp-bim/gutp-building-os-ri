import type { ControlExecutionState } from "@/lib/infra/grpc-client/use-control-execution";

/**
 * 制御 POST（`POST /points/{id}/control`）が失敗したときの HTTP ステータスを、操作フィードバック
 * の状態へ写像する純関数（#162）。
 *
 * 403（権限不足）と 503（ゲートウェイオフライン, #186）を汎用エラーと区別することで、ステータス
 * バーが「なぜ操作できないのか」を説明できるようにする。制御スキーマ違反（`dataType` を伴う 400）は
 * サーバ側が「何をどう直すか」を持っているため、その本文をそのまま見せる — 通知ポリシーの
 * 「バリデーション → 何をどう直すか」（`docs/architecture/oss-frontend-notification-policy.md`）。
 *
 * gRPC ストリーム（`useControlExecution`）は POST が 2xx を返した後にしか開かないため、403/503 は
 * ここ（POST の catch）でしか観測されない。
 */
export type ControlPostErrorStatus =
  "permission_denied" | "gateway_offline" | "failed";

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

/**
 * サーバが返した説明（`{ error, dataType }`）を取り出す。制御値の検証は twin の ControlSchema が
 * 正本で、許容範囲も enum の許容コードもサーバしか知らない（`ControlValueValidator`）。ここで文言を
 * 組み立て直すと二重管理になるため、サーバの説明をそのまま見せる。
 */
function controlSchemaViolationOf(error: unknown): string | undefined {
  const data = (error as { response?: { data?: unknown } })?.response?.data as
    { error?: unknown; dataType?: unknown } | undefined;
  // `dataType` is what distinguishes a schema violation from the other 400s PointController returns
  // ("value is required", an unsupported gateway binding, or a dispatch exception such as NATS being
  // down). Without it, telling the operator to fix their value would send them after the wrong thing.
  if (typeof data?.dataType !== "string") return undefined;
  return typeof data.error === "string" && data.error.trim() !== ""
    ? data.error
    : undefined;
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

  // 例: "value 45 is above the maximum 30" / "enum control value 9 is not one of [1, 2, 3, 4]"
  const schemaViolation = controlSchemaViolationOf(error);
  if (schemaViolation) {
    return {
      status: "failed",
      message: `指定した値は受け付けられません: ${schemaViolation}`,
    };
  }

  return {
    status: "failed",
    message: "制御信号の送信に失敗しました。",
  };
}
