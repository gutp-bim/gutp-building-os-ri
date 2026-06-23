import { Dialog } from "@headlessui/react";
import { useState } from "react";

export function BinaryOutputControlModal({
  isOpen,
  onClose,
  onControl,
  isLoading,
}: {
  isOpen: boolean;
  onClose: () => void;
  onControl: (value: boolean) => Promise<void>;
  isLoading: boolean;
}) {
  const [value, setValue] = useState(false);

  const handleSubmit = async () => {
    await onControl(value);
  };

  return (
    <Dialog open={isOpen} onClose={onClose} className="relative z-50">
      <div className="fixed inset-0 bg-black/30" aria-hidden="true" />
      <div className="fixed inset-0 flex items-center justify-center p-4">
        <Dialog.Panel className="bg-white rounded-lg p-6 w-full max-w-sm">
          <Dialog.Title className="text-lg font-medium mb-4">
            BinaryOutput制御
          </Dialog.Title>
          <div className="mb-4">
            <label className="block text-sm font-medium mb-1">値</label>
            <div className="flex gap-4">
              <label className="flex items-center">
                <input
                  type="radio"
                  name="binary-value"
                  value={0}
                  checked={!value}
                  onChange={() => setValue(false)}
                  className="mr-1"
                />
                OFF
              </label>
              <label className="flex items-center">
                <input
                  type="radio"
                  name="binary-value"
                  value={1}
                  checked={value}
                  onChange={() => setValue(true)}
                  className="mr-1"
                />
                ON
              </label>
            </div>
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
