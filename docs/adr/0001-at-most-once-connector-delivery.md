# ConnectorWorker は at-most-once 配信を採用する

ConnectorWorker がメッセージ処理に失敗しても、NATS JetStream へのメッセージ ack は無条件に行う。
センサーテレメトリは 1 件取りこぼしても許容範囲内であり、失敗メッセージを再配信し続けるループのほうが運用上の問題が大きいと判断した。

## Considered Options

**at-least-once（nack して再配信）**: 処理に失敗したメッセージを nack すれば NATS が再送するため、データ損失はゼロになる。
ただし、スキーマ不正・パース不能・OxiGraph 障害のような恒常的な失敗は無限再試行ループを引き起こし、
後続メッセージのブロックやアラート嵐につながる。

## Consequences

- `ConnectorWorkerBase` はハンドラー内で例外をキャッチしてログに記録し、処理を続行する。
- 処理失敗したメッセージは DLQ (`building-os.dlq.>`) に転送すること（未実装、将来対応）。
- 複数の ConnectorWorker が同じ subject を購読する場合、並列 dispatch (`Task.WhenAll`) を採用する。順序保証は不要。
