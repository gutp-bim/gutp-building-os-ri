namespace BuildingOs.Shared
{
    /// <summary>
    /// LoggerMiddleware用のオプション設定
    /// </summary>
    public class LoggerMiddlewareOption
    {
        /// <summary>
        /// ログ出力をフィルタリングするパス
        /// これらのパスを含むリクエストはログ出力しない
        /// </summary>
        public IEnumerable<string>? FilteringPaths { get; set; }

        /// <summary>
        /// リクエストボディをログに含めるかどうか
        /// </summary>
        public bool LogRequestBody { get; set; } = true;

        /// <summary>
        /// レスポンスボディをログに含めるかどうか
        /// </summary>
        public bool LogResponseBody { get; set; } = true;

        /// <summary>
        /// HTTPヘッダーをログに含めるかどうか
        /// </summary>
        public bool LogHeaders { get; set; } = false;

        /// <summary>
        /// リクエストデータのデシリアライズ処理
        /// nullの場合は生の文字列をログ出力
        /// </summary>
        public Func<string, object>? DeserializeRequest { get; set; }

        /// <summary>
        /// レスポンスデータのデシリアライズ処理
        /// nullの場合は生の文字列をログ出力
        /// </summary>
        public Func<string, object>? DeserializeResponse { get; set; }
    }
}