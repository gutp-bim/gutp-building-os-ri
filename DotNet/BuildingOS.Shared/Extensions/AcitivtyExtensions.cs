using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BuildingOs.Shared
{
    /// <summary>
    /// Activity用の拡張メソッド（Azure Monitor対応）
    /// </summary>
    public static class ActivityExtensions
    {
        /// <summary>
        /// ActivityからAzureLogRecordを生成する
        /// </summary>
        public static AzureLogRecord ToAzureLogRecord(
            this Activity activity, 
            object messageData, 
            string level = "Information")
        {
            if (activity == null)
            {
                var fallbackTraceId = Guid.NewGuid().ToString("N");
                return AzureLogRecord.Create(
                    traceId: fallbackTraceId,
                    spanId: Guid.NewGuid().ToString("N")[..16],
                    messageData: messageData,
                    level: level,
                    operationId: fallbackTraceId
                );
            }

            return AzureLogRecord.Create(
                traceId: activity.TraceId.ToString(),
                spanId: activity.SpanId.ToString(),
                messageData: messageData,
                level: level,
                operationId: activity.RootId ?? activity.TraceId.ToString()
            );
        }

        /// <summary>
        /// LogLevelをAzure用の文字列に変換
        /// </summary>
        public static string ToAzureLogLevel(this LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => "Verbose",
                LogLevel.Debug => "Verbose",
                LogLevel.Information => "Information",
                LogLevel.Warning => "Warning",
                LogLevel.Error => "Error",
                LogLevel.Critical => "Critical",
                _ => "Information"
            };
        }
    }
}