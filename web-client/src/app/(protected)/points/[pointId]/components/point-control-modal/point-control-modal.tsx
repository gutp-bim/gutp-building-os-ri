import { apiClient } from "@/lib/infra/aspida-client";
import { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { useControlExecution } from "@/lib/infra/grpc-client/use-control-execution";
import { useCallback, useMemo, useState } from "react";
import { AnalogOutputControlModal } from "./analog-output-control-modal";
import { BinaryOutputControlModal } from "./binary-output-control-modal";
import { ControlStatusBar } from "./control-status-bar";
import { getControlProtocol } from "./get-control-protocol";
import { MultiStateOutputControlModal } from "./multi-state-output-control-modal";
import { toControlValue } from "./to-control-value";

export function PointControlModal({
  pointDetail,
}: {
  pointDetail: PointDetail;
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
      startExecution(controlId);
    } catch {
      setIsLoading(false);
      setDirectResult("failed", "制御信号の送信に失敗しました。");
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
