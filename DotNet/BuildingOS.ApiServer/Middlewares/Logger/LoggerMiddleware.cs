using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BuildingOs.Shared
{
    /// <summary>
    /// ASP.NET Core用ロギングMiddleware（Azure Monitor対応）
    /// リクエスト/レスポンスのログ出力を行う
    /// Log出力時のエラーは握り潰す
    /// </summary>
    public class LoggerMiddleware
    {
        #region Fields

        private readonly RequestDelegate _next;
        private readonly ILogger<LoggerMiddleware> _logger;
        private readonly LoggerMiddlewareOption _option;

        #endregion

        #region Constructors

        public LoggerMiddleware(
            RequestDelegate next,
            ILogger<LoggerMiddleware> logger,
            LoggerMiddlewareOption option)
        {
            _next = next;
            _logger = logger;
            _option = option;
        }

        #endregion

        #region Methods

        public async Task InvokeAsync(HttpContext context)
        {
            // Logのフィルタリング
            var shouldSkip = _option.FilteringPaths?.Any(x =>
                context.Request.Path.Value?.Contains(x, StringComparison.OrdinalIgnoreCase) ?? false) ?? false;

            if (shouldSkip)
            {
                await _next(context);
                return;
            }

            // gRPCリクエストはボディ読み取り・レスポンス置き換えと非互換のためスキップ
            var contentType = context.Request.ContentType ?? "";
            if (contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            var activity = Activity.Current;
            var sw = new Stopwatch();
            var operationId = context.TraceIdentifier;

            // リクエストログ
            try
            {
                var requestData = await ExtractRequestDataAsync(context.Request);
                var logRecord = activity?.ToAzureLogRecord(requestData, "Information");
                if (logRecord != null)
                {
                    // カスタムディメンションに追加情報を設定
                    logRecord.CustomDimensions["requestId"] = context.TraceIdentifier;
                    logRecord.CustomDimensions["operationType"] = "HttpRequest";
                    logRecord.CustomDimensions["direction"] = "Incoming";
                    
                    logRecord.HttpRequest = new LogHttpRequest
                    {
                        Url = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}",
                        Method = context.Request.Method,
                        Protocol = context.Request.Protocol,
                        Host = context.Request.Host.ToString(),
                        Scheme = context.Request.Scheme,
                        UserAgent = context.Request.Headers.UserAgent.ToString(),
                        Referer = context.Request.Headers.Referer.ToString(),
                        RemoteIp = GetClientIpAddress(context)
                    };
                    
                    var json = JsonSerializer.Serialize(logRecord, JsonSerializerHelper.JsonSerializerOptions);
                    _logger.LogInformation(json);
                }
            }
            catch (Exception e) 
            { 
                _logger.LogError(e, "リクエストログ出力エラー: RequestId={RequestId}", context.TraceIdentifier); 
            }

            // レスポンス用のStreamを準備
            var originalBodyStream = context.Response.Body;
            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            sw.Start();
            
            try
            {
                await _next(context);
            }
            finally
            {
                sw.Stop();

                // レスポンスログ
                try
                {
                    var responseData = await ExtractResponseDataAsync(context.Response, responseBody);
                    var logLevel = GetLogLevelFromStatusCode(context.Response.StatusCode);
                    var logRecord = activity?.ToAzureLogRecord(responseData, logLevel.ToAzureLogLevel());
                    
                    if (logRecord != null)
                    {
                        // カスタムディメンションに追加情報を設定
                        logRecord.CustomDimensions["requestId"] = context.TraceIdentifier;
                        logRecord.CustomDimensions["operationType"] = "HttpResponse";
                        logRecord.CustomDimensions["direction"] = "Outgoing";
                        logRecord.CustomDimensions["success"] = context.Response.StatusCode < 400;
                        
                        logRecord.HttpRequest = new LogHttpRequest
                        {
                            Duration = sw.Elapsed.TotalMilliseconds,
                            Url = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}",
                            Method = context.Request.Method,
                            Protocol = context.Request.Protocol,
                            Host = context.Request.Host.ToString(),
                            Scheme = context.Request.Scheme,
                            StatusCode = context.Response.StatusCode,
                            ResponseSize = responseBody.Length,
                            UserAgent = context.Request.Headers.UserAgent.ToString(),
                            Referer = context.Request.Headers.Referer.ToString(),
                            RemoteIp = GetClientIpAddress(context)
                        };
                        
                        var json = JsonSerializer.Serialize(logRecord, JsonSerializerHelper.JsonSerializerOptions);
                        
                        // ステータスコードに応じてログレベルを変更
                        switch (logLevel)
                        {
                            case LogLevel.Warning:
                                _logger.LogWarning(json);
                                break;
                            case LogLevel.Error:
                                _logger.LogError(json);
                                break;
                            default:
                                _logger.LogInformation(json);
                                break;
                        }
                    }
                }
                catch (Exception e) 
                { 
                    _logger.LogError(e, "レスポンスログ出力エラー: RequestId={RequestId}", context.TraceIdentifier); 
                }

                // 元のStreamにレスポンスを書き戻し
                responseBody.Seek(0, SeekOrigin.Begin);
                await responseBody.CopyToAsync(originalBodyStream);
            }
        }

        private async Task<object> ExtractRequestDataAsync(HttpRequest request)
        {
            if (!_option.LogRequestBody)
            {
                return new 
                { 
                    Method = request.Method, 
                    Path = request.Path.Value,
                    QueryString = request.QueryString.Value,
                    ContentType = request.ContentType,
                    ContentLength = request.ContentLength
                };
            }

            // リクエストボディの読み取り
            request.EnableBuffering();
            var buffer = new byte[Convert.ToInt32(request.ContentLength ?? 0)];
            if (buffer.Length > 0)
            {
                await request.Body.ReadAsync(buffer, 0, buffer.Length);
            }
            var requestBody = Encoding.UTF8.GetString(buffer);
            request.Body.Position = 0; // ストリームをリセット

            var requestData = new
            {
                request.Method,
                request.ContentType,
                request.ContentLength,
                Path = request.Path.Value,
                QueryString = request.QueryString.Value,
                Headers = _option.LogHeaders ? 
                    request.Headers
                        .Where(h => !IsSecurityHeader(h.Key))
                        .ToDictionary(h => h.Key, h => h.Value.ToString()) : null,
                Body = _option.DeserializeRequest != null ? 
                    _option.DeserializeRequest(requestBody) : 
                    requestBody
            };

            return requestData;
        }

        private async Task<object> ExtractResponseDataAsync(HttpResponse response, MemoryStream responseBody)
        {
            if (!_option.LogResponseBody)
            {
                return new 
                { 
                    response.StatusCode,
                    response.ContentType,
                    ContentLength = responseBody.Length
                };
            }

            responseBody.Seek(0, SeekOrigin.Begin);
            var responseBodyText = await new StreamReader(responseBody).ReadToEndAsync();
            responseBody.Seek(0, SeekOrigin.Begin);

            var responseData = new
            {
                response.StatusCode,
                response.ContentType,
                ContentLength = responseBody.Length,
                Headers = _option.LogHeaders ? 
                    response.Headers
                        .Where(h => !IsSecurityHeader(h.Key))
                        .ToDictionary(h => h.Key, h => h.Value.ToString()) : null,
                Body = _option.DeserializeResponse != null ? 
                    _option.DeserializeResponse(responseBodyText) : 
                    responseBodyText
            };

            return responseData;
        }

        private static string GetClientIpAddress(HttpContext context)
        {
            // Azure Load Balancer / Application Gateway
            var xForwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(xForwardedFor))
            {
                return xForwardedFor.Split(',')[0].Trim();
            }

            // Cloudflare / Other proxies
            var xRealIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrEmpty(xRealIp))
            {
                return xRealIp;
            }

            // Azure specific headers
            var clientIp = context.Request.Headers["X-Azure-ClientIP"].FirstOrDefault();
            if (!string.IsNullOrEmpty(clientIp))
            {
                return clientIp;
            }

            return context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        }

        private static LogLevel GetLogLevelFromStatusCode(int statusCode)
        {
            return statusCode switch
            {
                >= 200 and < 300 => LogLevel.Information,
                >= 300 and < 400 => LogLevel.Information,
                >= 400 and < 500 => LogLevel.Warning,
                >= 500 => LogLevel.Error,
                _ => LogLevel.Information
            };
        }

        private static bool IsSecurityHeader(string headerName)
        {
            var securityHeaders = new[] 
            { 
                "Authorization", 
                "Cookie", 
                "Set-Cookie",
                "X-API-Key",
                "X-Auth-Token"
            };
            
            return securityHeaders.Any(h => 
                string.Equals(h, headerName, StringComparison.OrdinalIgnoreCase));
        }

        #endregion
    }
}
