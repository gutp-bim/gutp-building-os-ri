import { Button } from "@/components/ui/button";
import { InlineBanner } from "@/components/ui/inline-banner";
import { Dialog } from "@headlessui/react";

export function ColdDataDownloadModal({
  isOpen,
  onClose,
  startDate,
  endDate,
  onStartDateChange,
  onEndDateChange,
  onDownload,
  isLoading,
  error,
}: {
  isOpen: boolean;
  onClose: () => void;
  startDate: string;
  endDate: string;
  onStartDateChange: (date: string) => void;
  onEndDateChange: (date: string) => void;
  onDownload: () => void;
  isLoading: boolean;
  error?: string | null;
}) {
  return (
    <Dialog open={isOpen} onClose={onClose} className="relative z-50">
      <div className="fixed inset-0 bg-black/30" aria-hidden="true" />
      <div className="fixed inset-0 flex items-center justify-center p-4">
        <Dialog.Panel className="bg-white rounded-lg p-6 w-full max-w-md">
          <Dialog.Title className="text-lg font-medium mb-4">
            期間を選択してください
          </Dialog.Title>
          <div className="space-y-4">
            {error && (
              <InlineBanner tone="error" testId="cold-download-error">
                {error}
              </InlineBanner>
            )}
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                開始日時
              </label>
              <input
                type="datetime-local"
                value={startDate}
                onChange={(e) => onStartDateChange(e.target.value)}
                max={new Date().toISOString().slice(0, 16)}
                className="w-full border rounded-md px-3 py-2"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                終了日時
              </label>
              <input
                type="datetime-local"
                value={endDate}
                onChange={(e) => onEndDateChange(e.target.value)}
                max={new Date().toISOString().slice(0, 16)}
                className="w-full border rounded-md px-3 py-2"
              />
            </div>
            <div className="flex justify-end space-x-3 mt-6">
              <Button variant="secondary" onClick={onClose}>
                キャンセル
              </Button>
              <Button
                onClick={onDownload}
                disabled={!startDate || !endDate || isLoading}
              >
                {isLoading ? "ダウンロード中..." : "ダウンロード"}
              </Button>
            </div>
          </div>
        </Dialog.Panel>
      </div>
    </Dialog>
  );
}
