using System.Text.Json.Serialization;

namespace BuildingOs.Shared
{
    /// <summary>
    /// Azure Monitor用のログレコード
    /// </summary>
    public class AzureLogRecord
    {
        #region Properties

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("level")]
        public string Level { get; set; } = null!;

        [JsonPropertyName("message")]
        public object Message { get; set; } = null!;

        [JsonPropertyName("traceId")]
        public string TraceId { get; set; } = null!;

        [JsonPropertyName("spanId")]
        public string SpanId { get; set; } = null!;

        [JsonPropertyName("operationId")]
        public string OperationId { get; set; } = null!;

        [JsonPropertyName("httpRequest")]
        public LogHttpRequest HttpRequest { get; set; } = null!;

        [JsonPropertyName("customDimensions")]
        public Dictionary<string, object> CustomDimensions { get; set; } = null!;

        #endregion

        #region Methods

        public static AzureLogRecord Create(
            string traceId,
            string spanId,
            object messageData,
            string level = "Information",
            string? operationId = null
        ) => new()
        {
            TraceId = traceId,
            SpanId = spanId,
            OperationId = operationId ?? traceId,
            Level = level,
            Message = messageData,
            Timestamp = DateTime.UtcNow,
            CustomDimensions = new Dictionary<string, object>()
        };

        #endregion
    }

    /// <summary>
    /// ログ用のHTTPリクエスト情報
    /// 標準のHttpRequestと区別するためLogHttpRequestとして命名
    /// </summary>
    public class LogHttpRequest
    {
        #region Properties

        [JsonPropertyName("method")]
        public string Method { get; set; } = null!;

        [JsonPropertyName("url")]
        public string Url { get; set; } = null!;

        [JsonPropertyName("statusCode")]
        public int StatusCode { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; } // ミリ秒

        [JsonPropertyName("responseSize")]
        public long ResponseSize { get; set; }

        [JsonPropertyName("userAgent")]
        public string UserAgent { get; set; } = null!;

        [JsonPropertyName("referer")]
        public string Referer { get; set; } = null!;

        [JsonPropertyName("remoteIp")]
        public string RemoteIp { get; set; } = null!;

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = null!;

        [JsonPropertyName("host")]
        public string Host { get; set; } = null!;

        [JsonPropertyName("scheme")]
        public string Scheme { get; set; } = null!;

        #endregion

        #region Constructors

        public LogHttpRequest()
        {
            Protocol = "HTTP/1.1";
            StatusCode = 200;
        }

        #endregion
    }
}