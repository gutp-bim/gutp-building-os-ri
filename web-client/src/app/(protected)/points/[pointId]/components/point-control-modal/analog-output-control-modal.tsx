import { Dialog } from "@headlessui/react";
import { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { useState } from "react";

export function AnalogOutputControlModal({
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
  const min = pointDetail.point.minPresValue ?? 0;
  const max = pointDetail.point.maxPresValue ?? 100;
  const [value, setValue] = useState(min);

  const handleSubmit = async () => {
    await onControl(value);
  };

  return (
    <Dialog open={isOpen} onClose={onClose} className="relative z-50">
      <div className="fixed inset-0 bg-black/30" aria-hidden="true" />
      <div className="fixed inset-0 flex items-center justify-center p-4">
        <Dialog.Panel className="bg-white rounded-lg p-6 w-full max-w-sm">
          <Dialog.Title className="text-lg font-medium mb-4">
            AnalogOutput制御
          </Dialog.Title>
          <div className="mb-4">
            <label className="block text-sm font-medium mb-1">
              値（{min}～{max}）
            </label>
            <input
              type="number"
              min={min}
              max={max}
              value={value}
              onChange={(e) => setValue(Number(e.target.value))}
              className="w-full border rounded-md px-3 py-2"
            />
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
