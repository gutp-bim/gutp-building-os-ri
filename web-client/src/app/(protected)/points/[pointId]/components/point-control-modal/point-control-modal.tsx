import { apiClient } from "@/lib/infra/aspida-client";
import { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { useControlExecution } from "@/lib/infra/grpc-client/use-control-execution";
import {
  BacnetControlRequestBody,
  DkConnectControlRequestBody,
  DkConnectOperations,
} from "@/types/control";
import { useCallback, useMemo, useState } from "react";
import { AnalogOutputControlModal } from "./analog-output-control-modal";
import { BinaryOutputControlModal } from "./binary-output-control-modal";
import { ControlStatusBar } from "./control-status-bar";
import { DkConnectControlModal } from "./dk-connect-control-modal";
import { MultiStateOutputControlModal } from "./multi-state-output-control-modal";

// TODO: swagger + aspida 再生成後にこの型定義を削除
type ControlAcceptedResponse = { controlId: string };

/**
 * 制御プロトコルを判定する
 * BACnet: Point に BACnet固有フィールドが存在する
 * DkConnect: Device.gatewayId が "dkapi" で始まる
 */
const getControlProtocol = (
  pointDetail: PointDetail,
): "BACnet" | "DkConnect" | null => {
  const point = pointDetail.point;
  const device = pointDetail.device;

  // DkConnect判定: gatewayId に "dkapi" または "daikin" が含まれる（device-helper.ts と一致）
  if (device?.gatewayId) {
    const gwId = device.gatewayId.toLowerCase();
    if (gwId.includes("dkapi") || gwId.includes("daikin")) {
      return "DkConnect";
    }
  }

  // BACnet判定: Point に BACnet固有フィールドが存在する
  if (
    point.objectTypeBacnet ||
    point.instanceNoBacnet ||
    point.deviceIdBacnet
  ) {
    return "BACnet";
  }

  return null;
};

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

  const objectType = pointDetail.point.objectTypeBacnet?.toString();
  const controlProtocol = getControlProtocol(pointDetail);
  const controlSchema = pointDetail.controlSchema;

  // 制御可能性の判定:
  // DkConnect: プロトコル解決済み かつ (controlSchema あり または writable=true)
  // BACnet: controlSchema が必須（dataType に応じたモーダルがないと操作不能になるため）
  const canControl = useMemo(() => {
    if (controlProtocol === null) return false;
    if (controlProtocol === "DkConnect") {
      return controlSchema != null || (pointDetail.point?.writable ?? false);
    }
    return controlSchema != null;
  }, [controlProtocol, controlSchema, pointDetail]);

  // BACnet制御ハンドラー
  const handleBacnetControl = async (value: number | boolean) => {
    try {
      setIsLoading(true);

      const bacnetBody: BacnetControlRequestBody = {
        methodName: "setData",
        gatewayId: pointDetail.device!.gatewayId!,
        destDevId: pointDetail.point.deviceIdBacnet!,
        objectType: objectType ?? "",
        objectInstanceNo: pointDetail.point.instanceNoBacnet ?? 0,
        intValue: typeof value === "number" ? value : undefined,
        boolValue: typeof value === "boolean" ? value : undefined,
        priority: 8,
      };

      // TODO: swagger + aspida 再生成後に型キャストを削除
      const rawResult = await apiClient()
        .points._pointId(pointDetail.point.id)
        .control.$post({
          body: {
            controlType: "BACnet",
            body: JSON.stringify(bacnetBody),
          },
        });
      const accepted = rawResult as unknown as ControlAcceptedResponse;

      // モーダルを閉じて gRPC ストリームで結果を待機
      setIsOpen(false);
      setIsLoading(false);
      startExecution(accepted.controlId);
    } catch {
      setIsLoading(false);
      setDirectResult("failed", "制御信号の送信に失敗しました。");
    }
  };

  // DK Connect制御ハンドラー
  const handleDkConnectControl = async (operations: DkConnectOperations) => {
    try {
      setIsLoading(true);

      // equipmentIdをdevice.idから取得（DK ConnectのequipmentIdはDevice.idに対応）
      const equipmentId = pointDetail.device?.id;

      // equipmentIdが見つからない場合はエラー
      if (!equipmentId) {
        throw new Error(
          "equipmentIdが見つかりません。Device情報を確認してください。",
        );
      }

      const dkConnectBody: DkConnectControlRequestBody = {
        equipmentIdList: [equipmentId],
        operations: operations,
      };

      // TODO: swagger + aspida 再生成後に型キャストを削除
      const rawResult = await apiClient()
        .points._pointId(pointDetail.point.id)
        .control.$post({
          body: {
            controlType: "DkConnect",
            body: JSON.stringify(dkConnectBody),
          },
        });
      const accepted = rawResult as unknown as ControlAcceptedResponse;

      // モーダルを閉じて gRPC ストリームで結果を待機
      setIsOpen(false);
      setIsLoading(false);
      startExecution(accepted.controlId);
    } catch (error) {
      setIsLoading(false);
      setDirectResult(
        "failed",
        error instanceof Error
          ? error.message
          : "制御信号の送信に失敗しました。",
      );
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

      {/* DK Connect制御モーダル */}
      {controlProtocol === "DkConnect" && (
        <DkConnectControlModal
          isOpen={isOpen}
          onClose={handleClose}
          pointDetail={pointDetail}
          onControl={handleDkConnectControl}
          isLoading={isLoading}
        />
      )}
    </div>
  );
}
