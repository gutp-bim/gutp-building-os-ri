import type { GlossaryTerm, HelpEntry } from "./types";

/**
 * Seed help content (#149). Extend by adding entries/terms — the resolution logic and UI are driven
 * by these arrays. Keep definitions decisive and self-contained (they double as LLM prompt material).
 */
export const GLOSSARY: GlossaryTerm[] = [
  {
    term: "ポイント",
    reading: "ぽいんと",
    definition:
      "機器（Equipment）に属する計測点・制御点。SBCO オントロジーの sbco:PointExt に対応し、テレメトリの最小単位です。",
    category: "ontology",
  },
  {
    term: "機器",
    reading: "きき",
    definition:
      "空調・電力計・環境センサーなどの設備。SBCO の sbco:EquipmentExt に対応し、複数のポイントを持ちます。",
    category: "ontology",
  },
  {
    term: "メッセージレート",
    definition:
      "コネクターが直近1分間に処理したテレメトリ件数の合計（毎秒）。データ取り込みの活性度を表します。",
    category: "metric",
  },
  {
    term: "制御リクエスト数",
    definition:
      "直近5分間に発行された機器制御コマンドの件数。書き込み（制御）操作の量を表します。",
    category: "metric",
  },
  {
    term: "Ingress レート",
    definition:
      "取込口（MQTT / AMQP / gRPC gateway-ingress）が直近1分間に受信したメッセージ数（毎秒）。受理・拒否を問わず届いた量で、ツールチップに source 別の内訳が出ます（building_os.ingress.messages）。",
    category: "metric",
  },
  {
    term: "Validated レート",
    definition:
      "コネクターが検証を通して validated.telemetry へ発行した件数（毎秒, 直近1分）。Ingress から引き算した値ではなく独立に計測しています。負荷時は Ingress より遅れて追いつくため、両者の差はキューの滞留であって拒否ではありません。",
    category: "metric",
  },
  {
    term: "Rejected レート",
    definition:
      "取込口が拒否したメッセージ数（毎秒, 直近1分）と拒否率（rejected ÷ ingress）。ingress の result≠published を直接数えたもので、Ingress − Validated の差ではありません。ツールチップに理由（bad_payload / unknown_point など）別の内訳、累計件数は取込拒否件数画面に出ます。",
    category: "metric",
  },
  {
    term: "Event lag",
    definition:
      "値そのもののイベント時刻から Hot ストア到着までの遅延の p95（building_os.ingress.event_lag）。デバイス・ゲートウェイ・ネットワークも含む端から端までの遅れです。Consumer lag と並べて読みます — Event lag だけ高い＝Building OS に届く前（デバイス / ゲートウェイ / ネットワーク）で遅れている、両方高い＝Building OS 内部のキュー / バックプレッシャーを疑う。timestamp が欠落して受信時刻で補った値は遅延ほぼ 0 と記録されるため、その割合だけ過小評価になります。",
    category: "metric",
  },
  {
    term: "Consumer lag",
    definition:
      "JetStream のコンシューマーが raw ストリームに遅れている時間の p95（building_os.ingestion.lag）。NATS に載ってから取り出されるまでだけを測るので、上流で詰まっていても上がりません。Event lag が高くこちらが平常なら原因は Building OS の手前、両方高いなら Building OS 内部です。",
    category: "metric",
  },
  {
    term: "Parquet 鮮度",
    definition:
      "Parquet レイクへの flush 時点で、書き込んだ最新イベントがどれだけ古いかの p95（building_os.parquet_writer.freshness_lag）。目安は flush 間隔（PARQUET_FLUSH_INTERVAL）× 2 以内。タイムスタンプ不正で破棄した行（parquet_writer.dropped）が 1 件でもあれば警告にします。",
    category: "metric",
  },
  {
    term: "NATS pending",
    definition:
      "NATS JetStream の全コンシューマーの未処理メッセージ数の合計（recording rule nats:jetstream_consumer_pending:max）。取り込みのバックプレッシャー指標で、NATS exporter（observability プロファイル）が未配線なら表示されません。",
    category: "metric",
  },
  {
    term: "鮮度切れ閾値",
    definition:
      "テレメトリを「鮮度切れ（stale）」とみなすまでの秒数。アプリ設定で変更でき、表示や警告の判定に使われます。",
    category: "setting",
  },
  {
    term: "鮮度切れ",
    reading: "せんどぎれ",
    definition:
      "最終受信からの経過時間が判定閾値を超えた状態。過去には受信できているので値そのものは残っていますが、いま届いている保証はありません。値が閾値の内側かどうか（値異常）とは別の軸で、鮮度切れの間は値異常の判定を抑止します。",
    category: "concept",
  },
  {
    term: "欠測",
    reading: "けっそく",
    definition:
      "受信そのものが確認できない状態。一度も受信していない（受信履歴なし）か、ゲートウェイが切断しているために届いていないかのいずれかで、画面には理由も表示されます。値が無いので値異常の判定は行いません。",
    category: "concept",
  },
  {
    term: "期待更新周期",
    reading: "きたいこうしんしゅうき",
    definition:
      "そのポイントがどれくらいの間隔で値を送ってくるかの想定値（デジタルツインの sbco:interval）。鮮度の判定閾値はこの周期を基準に決まるため、5 秒周期のポイントと 1 時間周期のポイントを同じものさしで測らずに済みます。",
    category: "ontology",
  },
  {
    term: "鮮度判定",
    reading: "せんどはんてい",
    definition:
      "最終受信からの経過時間を判定閾値と比べて、最新 / 鮮度切れ / 欠測 を決める処理。閾値は「期待更新周期 × 倍率」で、期待更新周期が未設定のポイントはシステム既定の鮮度切れ閾値をそのまま使います（倍率は掛けません）。判定の正本はサーバー側にあり、画面は結果を表示するだけです。",
    category: "concept",
  },
  {
    term: "デジタルツイン",
    reading: "でじたるついん",
    definition:
      "建物 → フロア → 空間 → 機器 → ポイントの階層と、その静的メタデータを保持するグラフモデル。OxiGraph（SPARQL）で管理され、共有ポイントリストの正本（source of truth）です。単に「ツイン」とも呼びます。",
    category: "concept",
  },
  {
    term: "リソース",
    reading: "りそーす",
    definition:
      "デジタルツイン上のノードの総称。建物・フロア・空間・機器・ポイントのいずれかで、SBCO オントロジーのクラス（sbco:Building / sbco:Level / sbco:Room / sbco:EquipmentExt / sbco:PointExt）に対応します。/resources 画面はこの階層を辿るための入口です。",
    category: "concept",
  },
  {
    term: "ゲートウェイ",
    reading: "げーとうぇい",
    definition:
      "現場の設備（BACnet・OPC-UA 等）と BuildingOS の間を仲介する機器。共有ポイントリストに従ってプロトコル固有アドレスを point_id に解決し、テレメトリを送信（ingress）・制御を受信（egress）します。",
    category: "concept",
  },
  {
    term: "point_id",
    definition:
      "ポイントを一意に識別する BuildingOS 内の正本 ID。テレメトリ・制御・認可はすべてこの ID を主語に扱います。ゲートウェイ内のプロトコル固有アドレス（localId）とは別物で、両者はポイントリストで対応づけられます。",
    category: "id",
  },
  {
    term: "localId",
    reading: "ろーかるあいでぃー",
    definition:
      "ゲートウェイ側でポイントを指すプロトコル固有のローカルアドレス（例: BACnet の object/instance）。ゲートウェイがこれを BuildingOS の point_id に解決します。point_id が全体の正本、localId は現場側の別名です。",
    category: "id",
  },
  {
    term: "gateway_id",
    definition:
      "ゲートウェイを一意に識別する ID。テレメトリ・制御・ポイントリスト同期はこの ID を軸にゲートウェイ単位で振り分けられます。ツイン全体で一意（1 ゲートウェイ = 1 建物）で、そのゲートウェイが「所有」するポイントの範囲を決めます。個々の設備を指す device_id とは別の粒度です。",
    category: "id",
  },
  {
    term: "device_id",
    definition:
      "設備（機器 / Equipment）を一意に識別する ID。ポイントはいずれかの device に属し、複数の device が 1 つの gateway_id にぶら下がります。device_id は「どの設備か」、gateway_id は「どの中継器か」を表し、粒度が異なります。",
    category: "id",
  },
  {
    term: "GatewayIngress",
    definition:
      "テレメトリ取り込みの gRPC サービス（ConnectorWorker がホスト）。ゲートウェイから gateway_id + point_id + value + timestamp を受け取り、ツインで静的メタデータを補完して検証済みテレメトリとして配信します。制御は扱いません。",
    category: "architecture",
  },
  {
    term: "GatewayEgress",
    definition:
      "制御プレーンの gRPC サービス（GatewayBridge がホスト）。BuildingOS からゲートウェイへ制御コマンドを送る双方向ストリームで、テレメトリの入力（GatewayIngress）とは別経路・別ポートに分離されています。",
    category: "architecture",
  },
  {
    term: "ポイントリスト",
    reading: "ぽいんとりすと",
    definition:
      "ゲートウェイが担当するポイントの一覧（native アドレス・単位・書込可否・制御スキーマ等）。ツインが正本で、ゲートウェイは GET /gateways/{id}/pointlist で追従します。バージョンは内容ハッシュの ETag（revision）で表され、変化時のみ再取得されます。",
    category: "architecture",
  },
  {
    term: "リビジョン",
    reading: "りびじょん",
    definition:
      "ポイントリストの版数。内容から算出する順序非依存のハッシュ（\"sha256:...\" 形式の ETag）で表され、ポイントリストが変わったときだけ値が変わります。ゲートウェイは If-None-Match で問い合わせ、変化が無ければ 304 が返るので、無駄な再取得を避けられます。",
    category: "architecture",
  },
  {
    term: "SBCO",
    definition:
      "BuildingOS が採用するビル設備のオントロジー（語彙）。建物・フロア・空間・機器・ポイントを sbco:Building / sbco:Level / sbco:Room / sbco:EquipmentExt / sbco:PointExt として定義します。Brick / REC / IFC / DTDL 標準との対応は docs/architecture/standard-mapping.md にあります。",
    category: "architecture",
  },
  {
    term: "OxiGraph",
    reading: "おきしぐらふ",
    definition:
      "デジタルツインを格納する SPARQL / RDF グラフデータベース。建物階層とポイントの静的メタデータを保持し、リソース検索やポイント解決の基盤になります。",
    category: "architecture",
  },
  {
    term: "階層ストレージ",
    reading: "かいそうすとれーじ",
    definition:
      "テレメトリを用途別に分けて保存する方式。Hot（NATS KV の最新値・即時参照）と Warm/Cold（MinIO 上の Parquet レイクにまとめた履歴）を使い分けます。利用者視点では「最新値」と「履歴」の違いで、/telemetries/query が層を自動選択します。",
    category: "architecture",
  },
];

export const HELP_ENTRIES: HelpEntry[] = [
  {
    key: "platform.status",
    title: "システム稼働状態",
    body: [
      "各サービスの up/down とパイプライン KPI（Ingress / Validated / Rejected / Event lag / Consumer lag / Parquet 鮮度 / NATS pending / 制御リクエスト）を1画面に集約して表示します。",
      "サービスの up/down は /health のファンアウトで判定するため、Prometheus を起動していなくても確認できます。KPI は Prometheus 未配線時は空欄になります（observability プロファイルを有効にすると表示されます）。",
      "2 つの lag の読み分け: Event lag だけ高く Consumer lag が平常 → Building OS に届く前（デバイス / ゲートウェイ / ネットワーク）から遅れている。Event lag も Consumer lag も高い → Building OS 内部のキュー / バックプレッシャーを疑う（NATS pending も併せて確認）。",
      "Rejected は Ingress − Validated の差ではなく、拒否そのものを直接数えています。負荷時に Validated が Ingress より低いのはキューの滞留で、拒否ではありません。",
      "閾値を超えた KPI は黄色で表示されます。閾値はアプリ設定（/platform/settings の platform.kpi.*）で変更できます。",
    ],
    relatedTerms: [
      "Ingress レート",
      "Validated レート",
      "Rejected レート",
      "Event lag",
      "Consumer lag",
      "Parquet 鮮度",
      "NATS pending",
      "制御リクエスト数",
    ],
  },
  {
    key: "platform.config",
    title: "設定（実効値）",
    body: [
      "API サーバーの実効設定を読み取り専用で表示します。設定の source of truth は IaC / ArgoCD で、ここからは編集できません。",
      "シークレットは値を表示せず、設定済み / 未設定 のみ表示します。",
    ],
    relatedTerms: [],
  },
  {
    key: "platform.settings",
    title: "アプリ設定",
    body: [
      "フィーチャーフラグや閾値など、GitOps と衝突しないアプリ設定のみを編集できます。",
      "許可リストに登録されたキーのみ編集でき、値は型検証されます。「既定値に戻す」で上書きを取り消せます。",
    ],
    relatedTerms: ["鮮度切れ閾値"],
  },
  {
    key: "platform.ingressRejections",
    title: "取込拒否件数",
    body: [
      "gRPC gateway-ingress の受理/拒否ポリシー（Building 階層が未確定なポイントなど）による拒否件数を理由別に表示します。",
      "拒否は記録・計測されるだけで、キューイングや再送は行いません。twin の階層が修正され次第、ゲートウェイが送り続けているテレメトリーが次回の送信で自然に受理されます。",
      "サービス up/down と同様、Prometheus 未配線時は件数を表示できません（グレースフルデグレード）。",
    ],
    relatedTerms: [],
  },
  {
    key: "admin.gateways",
    title: "登録済みゲートウェイ",
    body: [
      "binding / 接続設定と pointlist 同期状態の観測画面です。binding や twin 上のポイント登録は GitOps / デジタルツインが正本のため、この画面自体に作成・登録操作はありません。",
      "ここに表示されるのは登録情報（binding / ポイント数 / pointlist リビジョン）です。ゲートウェイの接続状態（connected / last seen / 最終テレメトリ受信時刻）は表示しません。",
      "新しいゲートウェイをオンボードする手順(twin へのポイント登録・制御 binding・ingress/egress ポート・pointlist 同期)は docs/guides/gateway-onboarding-checklist.md に集約されています。",
    ],
    relatedTerms: [],
  },
  {
    key: "operator.health",
    title: "データ品質",
    body: [
      "ポイントごとに、データが届いているか（鮮度）と、届いた値が閾値の内側か（値異常）を一覧します。",
      "鮮度と値異常は別の軸です。最新のデータが届いていて、なお値が異常ということもあるため、チップも列も分けています。",
      "判定閾値は「期待更新周期 × 倍率」で決まります。期待更新周期が未設定のポイントはシステム既定の鮮度切れ閾値をそのまま使います。",
      "「同期中」と表示されているときは最終受信インデックスの走査中で、件数は暫定です。確定するまで欠測件数は表示しません。",
    ],
    relatedTerms: [
      "鮮度判定",
      "鮮度切れ",
      "欠測",
      "期待更新周期",
      "鮮度切れ閾値",
    ],
  },
];
