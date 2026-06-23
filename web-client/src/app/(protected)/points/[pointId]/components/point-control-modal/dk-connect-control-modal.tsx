import { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { DkConnectOperations } from "@/types/control";
import { Dialog } from "@headlessui/react";
import { useMemo, useState } from "react";

/**
 * pointNameからoperationsのキーへマッピングする
 */
const mapPointNameToOperationKey = (
  pointName: string | null | undefined,
): string | null => {
  if (!pointName) return null;

  const mapping: Record<string, string> = {
    "運転/停止": "onOff",
    設定温度: "setpoint",
    運転モード: "operatingMode",
    風量: "fanSpeed",
    風向: "airDirection",
    フィルターサインリセット: "filterSignReset",
    フィルタサインリセット: "filterSignReset",
    "手元リモコン運転/停止禁止": "RCOnOffProhibition",
    手元リモコン運転停止禁止: "RCOnOffProhibition",
    手元リモコン温度設定禁止: "RCSetpointProhibition",
    手元リモコン運転モード禁止: "RCOpModeProhibition",
    換気モード: "ventilationMode",
    換気量: "ventilationFanSpeed",
    室外機能力抑制状態: "outdoorPowerRatio",
  };

  return mapping[pointName] ?? null;
};

export function DkConnectControlModal({
  isOpen,
  onClose,
  pointDetail,
  onControl,
  isLoading,
}: {
  isOpen: boolean;
  onClose: () => void;
  pointDetail: PointDetail;
  onControl: (operations: DkConnectOperations) => Promise<void>;
  isLoading: boolean;
}) {
  const [boolValue, setBoolValue] = useState<boolean>(true);
  const [numberValue, setNumberValue] = useState<number>(26.0);
  const [enumValue, setEnumValue] = useState<number>(1);
  const [lastControlTime, setLastControlTime] = useState<number>(0);

  const controlSchema = pointDetail.controlSchema;
  const pointName = pointDetail.point.name;
  const operationKey = mapPointNameToOperationKey(pointName);
  const dataType = controlSchema?.dataType;

  // enumLabelsをパース
  const enumOptions = useMemo(() => {
    if (dataType !== "enum" || !controlSchema?.enumLabels) {
      return [];
    }
    try {
      const parsed = JSON.parse(controlSchema.enumLabels) as Record<
        string,
        string
      >;
      return Object.entries(parsed).map(([key, label]) => ({
        value: Number(key),
        label,
      }));
    } catch {
      return [];
    }
  }, [dataType, controlSchema?.enumLabels]);

  // 数値入力のバリデーション（設定温度用）
  const validateNumber = (value: number): number => {
    if (value > 127.9) return 127.9;
    if (value < -127.9) return -127.9;
    return Math.round(value * 10) / 10;
  };

  const handleSubmit = async () => {
    // レート制限チェック（3秒以内の連続送信を防ぐ）
    const now = Date.now();
    const timeSinceLastControl = now - lastControlTime;

    if (timeSinceLastControl < 3000) {
      alert(
        `前回の操作から${Math.ceil((3000 - timeSinceLastControl) / 1000)}秒お待ちください`,
      );
      return;
    }

    if (!operationKey) {
      alert("制御種別が特定できません");
      return;
    }

    // 該当項目のみを含むoperationsを構築
    const operations: DkConnectOperations = {};

    switch (operationKey) {
      case "onOff":
        operations.onOff = boolValue;
        break;
      case "setpoint":
        operations.setpoint = validateNumber(numberValue);
        break;
      case "operatingMode":
        operations.operatingMode = enumValue as 1 | 2 | 3 | 4 | 5 | 6;
        break;
      case "fanSpeed":
        operations.fanSpeed = enumValue as 1 | 2 | 3 | 4;
        break;
      case "airDirection":
        operations.airDirection = enumValue as 1 | 2 | 3 | 4 | 5 | 6;
        break;
      case "filterSignReset":
        operations.filterSignReset = boolValue;
        break;
      case "RCOnOffProhibition":
        operations.RCOnOffProhibition = boolValue;
        break;
      case "ventilationMode":
        operations.ventilationMode = enumValue as 1 | 2 | 3;
        break;
      case "ventilationFanSpeed":
        operations.ventilationFanSpeed = enumValue as 1 | 2 | 3 | 4 | 5 | 6;
        break;
      case "outdoorPowerRatio":
        operations.outdoorPowerRatio = enumValue as 40 | 70 | 100;
        break;
      default:
        alert(`未対応の制御種別: ${operationKey}`);
        return;
    }

    await onControl(operations);
    setLastControlTime(now);
  };

  // pointNameをそのままラベルとして使用（日本語名が入っているため）
  const getPointLabel = (): string => {
    return pointName ?? "不明";
  };

  // booleanのラベルを取得
  const getBooleanLabels = (): { trueLabel: string; falseLabel: string } => {
    switch (operationKey) {
      case "onOff":
        return { trueLabel: "運転", falseLabel: "停止" };
      case "filterSignReset":
        return { trueLabel: "リセット", falseLabel: "維持" };
      case "RCOnOffProhibition":
        return { trueLabel: "禁止", falseLabel: "許可" };
      default:
        return { trueLabel: "ON", falseLabel: "OFF" };
    }
  };

  return (
    <Dialog open={isOpen} onClose={onClose} className="relative z-50">
      <div className="fixed inset-0 bg-black/30" aria-hidden="true" />
      <div className="fixed inset-0 flex items-center justify-center p-4">
        <Dialog.Panel className="bg-white rounded-lg p-6 w-full max-w-md max-h-[90vh] overflow-y-auto">
          <Dialog.Title className="text-xl font-semibold mb-4">
            DK Connect 制御 - {getPointLabel()}
          </Dialog.Title>

          <div className="space-y-4">
            {/* Boolean型: トグルボタン */}
            {dataType === "boolean" && (
              <div>
                <label className="block text-sm font-medium mb-2">
                  {getPointLabel()}
                </label>
                <div className="flex gap-2">
                  <button
                    type="button"
                    onClick={() => setBoolValue(true)}
                    className={`flex-1 px-4 py-2 rounded-md cursor-pointer ${
                      boolValue
                        ? "bg-blue-500 text-white"
                        : "bg-gray-200 text-gray-700"
                    }`}
                  >
                    {getBooleanLabels().trueLabel}
                  </button>
                  <button
                    type="button"
                    onClick={() => setBoolValue(false)}
                    className={`flex-1 px-4 py-2 rounded-md cursor-pointer ${
                      !boolValue
                        ? "bg-blue-500 text-white"
                        : "bg-gray-200 text-gray-700"
                    }`}
                  >
                    {getBooleanLabels().falseLabel}
                  </button>
                </div>
              </div>
            )}

            {/* Number型: 数値入力（設定温度） */}
            {dataType === "number" && operationKey === "setpoint" && (
              <div>
                <label className="block text-sm font-medium mb-2">
                  設定温度: {numberValue}℃
                </label>
                <input
                  type="range"
                  min="-127.9"
                  max="127.9"
                  step="0.5"
                  value={numberValue}
                  onChange={(e) => setNumberValue(parseFloat(e.target.value))}
                  className="w-full cursor-pointer"
                />
                <div className="flex justify-between text-xs text-gray-500 mt-1">
                  <span>-127.9℃</span>
                  <span>127.9℃</span>
                </div>
                <input
                  type="number"
                  min="-127.9"
                  max="127.9"
                  step="0.1"
                  value={numberValue}
                  onChange={(e) => {
                    const value = parseFloat(e.target.value);
                    if (!isNaN(value)) {
                      setNumberValue(validateNumber(value));
                    }
                  }}
                  className="w-full px-3 py-2 border border-gray-300 rounded-md mt-2 cursor-text"
                />
                <p className="text-xs text-gray-500 mt-1">
                  範囲: -127.9℃ ～ 127.9℃（小数第二位を四捨五入）
                </p>
              </div>
            )}

            {/* Enum型: セレクトボックス */}
            {dataType === "enum" && enumOptions.length > 0 && (
              <div>
                <label className="block text-sm font-medium mb-2">
                  {getPointLabel()}
                </label>
                <select
                  value={enumValue}
                  onChange={(e) => setEnumValue(parseInt(e.target.value))}
                  className="w-full px-3 py-2 border border-gray-300 rounded-md cursor-pointer"
                >
                  {enumOptions.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
              </div>
            )}

            {/* 未対応のdataType */}
            {!dataType && (
              <div className="bg-red-50 border border-red-200 rounded p-3">
                <p className="text-sm text-red-800">
                  このポイントの制御スキーマが見つかりません。
                </p>
              </div>
            )}
          </div>

          <div className="mt-6 flex justify-end space-x-3">
            <button
              onClick={onClose}
              disabled={isLoading}
              className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 rounded-md hover:bg-gray-200 cursor-pointer disabled:opacity-50"
            >
              キャンセル
            </button>
            <button
              onClick={handleSubmit}
              disabled={isLoading || !dataType}
              className="px-4 py-2 text-sm font-medium text-white bg-blue-500 rounded-md hover:bg-blue-600 cursor-pointer disabled:opacity-50"
            >
              {isLoading ? "送信中..." : "制御信号を送信"}
            </button>
          </div>
        </Dialog.Panel>
      </div>
    </Dialog>
  );
}
