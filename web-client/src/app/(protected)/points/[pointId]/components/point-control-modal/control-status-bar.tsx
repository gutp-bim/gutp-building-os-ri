import type { ControlExecutionState } from "@/lib/infra/grpc-client/use-control-execution";

export function ControlStatusBar({
  state,
  onCancel,
  onDismiss,
}: {
  state: ControlExecutionState;
  onCancel: () => void;
  onDismiss: () => void;
}) {
  if (state.status === "idle") return null;

  return (
    <div
      className={`flex items-start justify-between px-4 py-3 rounded-md border ${styleMap[state.status].container}`}
    >
      <div
        className={`flex items-center gap-2 text-sm ${styleMap[state.status].text}`}
      >
        {state.status === "executing" && (
          <>
            <Spinner />
            <span>
              制御実行中... ({state.elapsedSeconds}秒)
            </span>
          </>
        )}
        {state.status === "success" && (
          <>
            <CheckIcon />
            <span>制御が正常に完了しました</span>
          </>
        )}
        {state.status === "failed" && (
          <>
            <CrossIcon />
            <div>
              <p>制御に失敗しました</p>
              {state.message && (
                <p className="text-xs mt-0.5 opacity-80">{state.message}</p>
              )}
            </div>
          </>
        )}
        {state.status === "timeout" && (
          <>
            <WarningIcon />
            <div>
              <p>制御結果を確認できませんでした</p>
              <p className="text-xs mt-0.5 opacity-80">
                デバイスの状態を直接確認してください
              </p>
            </div>
          </>
        )}
        {state.status === "cancelled" && (
          <div>
            <p>結果の確認をキャンセルしました</p>
            <p className="text-xs mt-0.5 opacity-80">
              制御コマンドは送信済みです
            </p>
          </div>
        )}
      </div>

      <div className="flex-shrink-0 ml-3">
        {state.status === "executing" ? (
          <button
            onClick={onCancel}
            className="text-sm text-blue-600 hover:text-blue-800 cursor-pointer whitespace-nowrap"
          >
            キャンセル
          </button>
        ) : (
          <button
            onClick={onDismiss}
            className="text-gray-400 hover:text-gray-600 cursor-pointer"
            aria-label="閉じる"
          >
            <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        )}
      </div>
    </div>
  );
}

const styleMap = {
  executing: {
    container: "bg-blue-50 border-blue-200",
    text: "text-blue-800",
  },
  success: {
    container: "bg-green-50 border-green-200",
    text: "text-green-800",
  },
  failed: {
    container: "bg-red-50 border-red-200",
    text: "text-red-800",
  },
  timeout: {
    container: "bg-amber-50 border-amber-200",
    text: "text-amber-800",
  },
  cancelled: {
    container: "bg-gray-50 border-gray-200",
    text: "text-gray-700",
  },
} as const;

function Spinner() {
  return (
    <svg
      className="animate-spin h-4 w-4 flex-shrink-0"
      xmlns="http://www.w3.org/2000/svg"
      fill="none"
      viewBox="0 0 24 24"
    >
      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
    </svg>
  );
}

function CheckIcon() {
  return (
    <svg className="h-4 w-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
    </svg>
  );
}

function CrossIcon() {
  return (
    <svg className="h-4 w-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
    </svg>
  );
}

function WarningIcon() {
  return (
    <svg className="h-4 w-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
    </svg>
  );
}
