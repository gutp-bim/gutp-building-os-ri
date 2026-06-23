# GatewayBridge は per-gateway NATS ルーティングでステートレス水平スケールする

GatewayBridge は、外部ゲートウェイ（BOWS）が張る gateway ごとの双方向 gRPC ストリーム（`GatewayEgress`）を
収容する egress 制御プレーンである。**どのレプリカにストリームが載っても制御コマンドが到達する**ことを、
LB のスティッキー性ではなく **per-gateway NATS subject ルーティング**で保証する。これによりレプリカは
ステートレスで水平スケール可能。本 ADR はこの設計が成立するための条件を明文化する。

## Considered Options

**LB スティッキー / コンシステントハッシュで gateway→レプリカを固定する**: API から特定レプリカへ直接届ける案。
LB 層に gateway 親和性の状態を持たせる必要があり、レプリカ増減・再接続でルーティングが崩れる。ステートフルになり
水平スケールと運用が複雑化するため不採用。

## Decision（成立条件）

以下がすべて満たされることを前提に「ステートレス水平スケール」が成立する。

1. **ルーティング = per-gateway NATS subject**。ストリームを収容したレプリカが
   `building-os.control.request.gw.{gatewayId}` を購読する。ApiServer はこの subject に **NATS request** で発行するため、
   どのレプリカが収容していても収容レプリカに届く（LB 選択に非依存）。出典:
   `DotNet/BuildingOS.GatewayBridge/Infrastructure/NatsEgressCommandBus.cs`,
   `docs/oss-gateway-bridge-infra.md`。
2. **収容の一意性（同一プロセス内）**は `IGatewayConnectionRegistry.TryRegister(gatewayId)` で担保。多重 Connect は拒否。
   出典: `DotNet/BuildingOS.GatewayBridge/Infrastructure/GatewayConnectionRegistry.cs`。
   - **留意（事実）**: 現状のレジストリは **プロセスローカル**（`ConcurrentDictionary`）。同一 `gatewayId` が**別レプリカ**へ
     同時接続した場合、両レプリカが当該 subject を購読しうる。NATS request は最初の応答者で解決されるため二重配送は避けられるが、
     **クラスタ全体での収容一意性は LB/ingress 側（1 ストリーム=1 レプリカ）に依存**する。クラスタ越しの一意性が要件なら
     分散レジストリ化は将来課題。
3. **切断時の unregister**: Connect の成功後は `try/finally` で必ず `Unregister(gatewayId)` し、購読も `await using` で破棄する。
   `bus.SubscribeAsync` が例外で落ちた場合も unregister される。出典:
   `DotNet/BuildingOS.GatewayBridge/Services/GatewayEgressService.cs`。
4. **オフライン即時 503**: per-gateway は NATS *request* で送られ、購読者がいない（未接続）と no-responders となり、
   ApiServer は結果タイムアウトを待たず **503** を返す（`control.requests{result=gateway_offline}`）。出典: #186,
   `NatsEgressCommandBus.cs`, `CLAUDE.md`。
5. **コマンドタイムアウト**: 接続中だが遅い場合の往復は `CONTROL_RESULT_TIMEOUT_SEC`（既定 10s）が上限。
   ack タイムアウト（レプリカ在席だが遅い）は配送済み扱いとし、結果タイムアウトをバックストップとする。出典: `CLAUDE.md`。
6. **結果返却**: ゲートウェイの `ControlResult` は `building-os.control.result.{controlId}` に publish され、
   ApiServer 既存の `WaitForResult` が受ける。`control_id` 単位で対応づく。
7. **同一ストリームへの並行 write 直列化**: down 方向（コマンド + pointlist 更新 push）の gRPC 書き込みは
   `SemaphoreSlim(1,1)` で直列化（gRPC は同一ストリームへの並行 write を許さない）。出典: `GatewayEgressService.cs`。
8. **購読ライフサイクル**: コマンド subject と pointlist 更新 subject（`building-os.pointlist.updated.gw.{id}`）は
   ストリーム生存期間に束ねられ、切断/キャンセルで破棄される（`IAsyncDisposable`）。出典: `NatsEgressCommandBus.cs`。

## Consequences

- レプリカ増減・ローリング更新は安全（再接続で別レプリカに載り、subject 購読が follow する）。LB は L4/L7 接続レベルで十分、
  スティッキー不要。
- **再送 / 重複実行防止**は現状「エフェメラルな request-reply + `control_id` 冪等キー」に依存し、**耐久キューによる自動再送は持たない**
  （障害中の stale コマンドは復旧後に勝手に実行されない＝物理制御の安全側、E6 stale-replay=0）。確実な再送が要件なら別途設計。
- **未決 / 将来課題**: (a) クラスタ越しの収容一意性（分散レジストリ）、(b) ゲートウェイ側での `control_id` 冪等チェックの明文化、
  (c) ack/結果タイムアウトのメトリクス SLO。運用要件が固まり次第、本 ADR を更新する。
- 関連: `docs/oss-egress-gateway-bridge-plan.md`, `docs/gateway-bridge-ingress-egress-split.md`,
  `docs/oss-gateway-bridge-infra.md`, `DotNet/BuildingOS.GatewayBridge/README.md`。
