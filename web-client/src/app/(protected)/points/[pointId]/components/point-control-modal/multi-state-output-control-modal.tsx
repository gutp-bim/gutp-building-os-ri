import { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { Dialog } from "@headlessui/react";
import { useMemo, useState } from "react";

export function MultiStateOutputControlModal({
  isOpen,
  onClose,
  pointDetail,
  onControl,
  isLoading,
}: {
  isOpen: boolean;
  onClose: () => void;
  pointDetail: PointDetail;
  onControl: (value: number) => Promise<void>;
  isLoading: boolean;
}) {
  const [value, setValue] = useState(1);

  // enumLabelsをパースしてkey-value配列を生成
  // enumLabels format: {"1":"Off","2":"Low","3":"Medium","4":"High"}
  const enumOptions = useMemo(() => {
    const enumLabels = pointDetail.controlSchema?.enumLabels;
    if (enumLabels) {
      try {
        const parsed = JSON.parse(enumLabels) as Record<string, string>;
        return Object.entries(parsed).map(([key, label]) => ({
          value: Number(key),
          label,
        }));
      } catch {
        // パース失敗時はフォールバック
      }
    }
    // フォールバック: point.labelsを使用（後方互換性）
    const labels = pointDetail.point.labels?.split(",") ?? [];
    return labels.map((label, idx) => ({
      value: idx + 1,
      label: label.trim(),
    }));
  }, [pointDetail.controlSchema?.enumLabels, pointDetail.point.labels]);

  const handleSubmit = async () => {
    await onControl(value);
  };

  return (
    <Dialog open={isOpen} onClose={onClose} className="relative z-50">
      <div className="fixed inset-0 bg-black/30" aria-hidden="true" />
      <div className="fixed inset-0 flex items-center justify-center p-4">
        <Dialog.Panel className="bg-white rounded-lg p-6 w-full max-w-sm">
          <Dialog.Title className="text-lg font-medium mb-4">
            MultiStateOutput制御
          </Dialog.Title>
          <div className="mb-4">
            <label className="block text-sm font-medium mb-1">選択肢</label>
            <select
              value={value}
              onChange={(e) => setValue(Number(e.target.value))}
              className="w-full border rounded-md px-3 py-2"
            >
              {enumOptions.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </div>
          <div className="flex justify-end space-x-3">
            <button
              onClick={onClose}
              className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 rounded-md hover:bg-gray-200 cursor-pointer"
              disabled={isLoading}
            >
              キャンセル
            </button>
            <button
              onClick={handleSubmit}
              className="px-4 py-2 text-sm font-medium text-white bg-blue-500 rounded-md hover:bg-blue-600 cursor-pointer disabled:opacity-50"
              disabled={isLoading}
            >
              {isLoading ? "送信中..." : "送信"}
            </button>
          </div>
        </Dialog.Panel>
      </div>
    </Dialog>
  );
}
