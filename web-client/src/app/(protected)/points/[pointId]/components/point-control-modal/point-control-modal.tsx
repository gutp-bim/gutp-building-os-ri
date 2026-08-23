import { apiClient } from "@/lib/infra/aspida-client";
import {
  useControlExecution,
  type ControlExecutionState,
} from "@/lib/infra/grpc-client/use-control-execution";
import type { PointDetailResource } from "@/lib/resources/types";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AnalogOutputControlModal } from "./analog-output-control-modal";
import { BinaryOutputControlModal } from "./binary-output-control-modal";
import { controlPostErrorResult } from "./control-post-error";
import { ControlStatusBar } from "./control-status-bar";
import { getControlProtocol } from "./get-control-protocol";
import { leavesAuditTrail } from "./leaves-audit-trail";
import { MultiStateOutputControlModal } from "./multi-state-output-control-modal";
import { toControlValue } from "./to-control-value";

export function PointControlModal({
  pointDetail,
  onControlSettled,
}: {
  pointDetail: PointDetailResource;
  /**
   * Called once per control that reached the server, when it settles. The point detail page uses it
   * to refresh 制御履歴 so the command the operator just ran is visible without a reload (#162).
   */
  onControlSettled?: () => void;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const [isLoading, setIsLoading] = useState(false);

  const {
    state: executionState,
    startExecution,
    cancel,
    dismiss,
    setDirectResult,
    isExecuting,
  } = useControlExecution();

  // Whether the last attempt got far enough for the server to open an audit row. A 403 (and any
  // other outright POST rejection) never does; a 503 does, because the server closes the row it
  // opened (#333). Only the modal sees how the result was reached, so the flag lives here.
  const [dispatched, setDispatched] = useState(false);

  // Fire onControlSettled once per *result*, not once per status: two consecutive controls can
  // settle in the same status (retry after a failure, or a second success inside the auto-dismiss
  // window), and comparing statuses would swallow the second one — leaving 制御履歴 stale, which is
  // the bug this exists to prevent. useControlExecution allocates a new state object per result, so
  // reference identity is exactly "this is a new result".
  const settledRef = useRef<ControlExecutionState | null>(null);
  useEffect(() => {
    if (!leavesAuditTrail(executionState, dispatched)) return;
    if (settledRef.current === executionState) return;
    settledRef.current = executionState;
    onControlSettled?.();
  }, [executionState, dispatched, onControlSettled]);

  const controlProtocol = getControlProtocol(pointDetail);
  const controlSchema = pointDetail.controlSchema;

  // 制御可能性の判定: BACnet は controlSchema が必須
  // （dataType に応じたモーダルがないと操作不能になるため）
  const canControl = useMemo(() => {
    if (controlProtocol === null) return false;
    return controlSchema != null;
  }, [controlProtocol, controlSchema]);

  // BACnet制御ハンドラー（Kandt ゲートウェイ経由の制御も含む）
  // ControlTypeResolver がゲートウェイ/BACnetアドレス指定をサーバー側で解決するため、
  // クライアントは点の値のみを送信する(#154)。
  const handleBacnetControl = async (value: number | boolean) => {
    try {
      setIsLoading(true);

      const { controlId } = await apiClient()
        .points._pointId(pointDetail.point.id)
        .control.$post({
          body: { value: toControlValue(value) },
        });

      // モーダルを閉じて gRPC ストリームで結果を待機
      setIsOpen(false);
      setIsLoading(false);
      setDispatched(true);
      startExecution(controlId);
    } catch (error) {
      setIsLoading(false);
      // 403（権限不足）/ 503（gateway offline, #186）を汎用失敗と区別して説明する（#162）。
      const { status, message } = controlPostErrorResult(
        error,
        pointDetail.point.id,
      );
      // A rejected POST leaves no audit row — except 503, where the server opened one and closed it
      // out as failed. leavesAuditTrail reads gateway_offline directly, so this only has to say that
      // nothing else here was dispatched.
      setDispatched(false);
      setDirectResult(status, message);
    }
  };

  const handleClose = useCallback(() => {
    if (isLoading) return;
    setIsOpen(false);
  }, [isLoading]);

  return (
    <div className="flex flex-col gap-2">
      {/* ステータスバー */}
      <ControlStatusBar
        state={executionState}
        onCancel={cancel}
        onDismiss={dismiss}
        showAuditLink={leavesAuditTrail(executionState, dispatched)}
      />

      {/* 制御ボタン: executing 中は非表示 */}
      {!isExecuting &&
        (canControl ? (
          <button
            className="bg-blue-500 hover:bg-blue-600 text-white px-4 py-2 rounded-md cursor-pointer w-fit"
            onClick={() => setIsOpen(true)}
          >
            制御信号を送信
          </button>
        ) : (
          <button
            className="bg-gray-400 text-white px-4 py-2 rounded-md cursor-not-allowed w-fit"
            disabled
          >
            制御不可
          </button>
        ))}

      {/* BACnet制御モーダル */}
      {controlProtocol === "BACnet" && controlSchema?.dataType === "number" && (
        <AnalogOutputControlModal
          isOpen={isOpen}
          onClose={handleClose}
          pointDetail={pointDetail}
          onControl={handleBacnetControl}
          isLoading={isLoading}
        />
      )}
      {controlProtocol === "BACnet" &&
        controlSchema?.dataType === "boolean" && (
          <BinaryOutputControlModal
            isOpen={isOpen}
            onClose={handleClose}
            onControl={handleBacnetControl}
            isLoading={isLoading}
          />
        )}
      {controlProtocol === "BACnet" && controlSchema?.dataType === "enum" && (
        <MultiStateOutputControlModal
          isOpen={isOpen}
          onClose={handleClose}
          pointDetail={pointDetail}
          onControl={handleBacnetControl}
          isLoading={isLoading}
        />
      )}
    </div>
  );
}
