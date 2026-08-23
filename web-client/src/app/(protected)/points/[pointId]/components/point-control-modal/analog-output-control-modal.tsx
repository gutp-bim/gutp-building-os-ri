import type { PointDetailResource } from "@/lib/resources/types";
import { Dialog } from "@headlessui/react";
import { useState } from "react";
import {
  controlRangeLabel,
  initialControlValue,
  resolveControlRange,
} from "./resolve-control-range";

export function AnalogOutputControlModal({
  isOpen,
  onClose,
  pointDetail,
  onControl,
  isLoading,
}: {
  isOpen: boolean;
  onClose: () => void;
  pointDetail: PointDetailResource;
  onControl: (value: number) => Promise<void>;
  isLoading: boolean;
}) {
  // 制御範囲の正は ControlSchema。詳細は resolve-control-range.ts を参照 (#298)。
  const { min, max } = resolveControlRange(pointDetail);
  const rangeLabel = controlRangeLabel({ min, max });
  const [value, setValue] = useState(initialControlValue({ min, max }));

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
            <label
              className="block text-sm font-medium mb-1"
              htmlFor="analog-control-value"
            >
              値{rangeLabel ? `（${rangeLabel}）` : ""}
            </label>
            <input
              id="analog-control-value"
              type="number"
              min={min}
              max={max}
              value={value}
              onChange={(e) => setValue(Number(e.target.value))}
              className="w-full border rounded-md px-3 py-2"
            />
            {rangeLabel === null && (
              <p
                className="mt-1 text-xs text-gray-500"
                data-testid="range-unknown"
              >
                この点には制御範囲が登録されていません。
              </p>
            )}
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
