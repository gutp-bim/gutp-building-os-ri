# Building OS Load Test System - Architecture Design

## 1. システム全体構成図

```mermaid
graph TB
    CLI[CLI Interface<br/>run_test.py] --> Config[ConfigManager<br/>設定読み込み・検証]
    CLI --> Orchestrator[TestOrchestrator<br/>テスト実行統括]

    Config --> |YAML/JSON| ConfigFiles[設定ファイル<br/>configs/scenarios/]
    Config --> |環境変数| EnvVars[.env ファイル<br/>IoT Hub接続文字列]

    Orchestrator --> Scenarios[シナリオクラス群]
    Orchestrator --> DeviceFactory[DeviceFactory<br/>デバイス生成管理]

    Scenarios --> ScalingScenario[DeviceScalingScenario<br/>デバイス数スケーリング]
    Scenarios --> FreqScenario[MessageFrequencyScenario<br/>メッセージ頻度負荷]
    Scenarios --> DataScenario[DataSizeLoadScenario<br/>データサイズ負荷]

    DeviceFactory --> Devices[デバイス実装群]
    Devices --> BacnetDevice[BacnetDevice]
    Devices --> HvacDevice[HvacDevice]
    Devices --> EnvDevice[EnvironmentalDevice]
    Devices --> ElectricDevice[ElectricDevice]
    Devices --> BehaviorDevice[BehaviorDevice]

    Devices --> |継承| BaseDevice[BaseDevice<br/>Azure IoT Hub接続]
    BaseDevice --> MsgGen[MessageGenerator<br/>メッセージ生成]
    BaseDevice --> |送信| IoTHub[Azure IoT Hub]

    Orchestrator --> |結果保存| Results[results/<br/>JSON結果ファイル]
```

## 2. 処理フロー詳細図

### 2.1 メイン処理フロー

```mermaid
sequenceDiagram
    participant User
    participant CLI as run_test.py
    participant Config as ConfigManager
    participant Orch as TestOrchestrator
    participant Scenario
    participant Factory as DeviceFactory
    participant Device as BaseDevice
    participant IoT as IoT Hub

    User->>CLI: python run_test.py --scenario device_scaling --step 1,2
    CLI->>Config: load_config(scenario, options)
    Config->>Config: 環境変数読み込み(.env)
    Config->>Config: JSON設定ファイル読み込み
    Config->>Config: Pydanticモデル検証
    Config-->>CLI: 設定オブジェクト

    CLI->>Orch: TestOrchestrator(config)
    Orch->>Orch: メトリクス収集器初期化
    Orch->>Factory: DeviceFactory(接続文字列)

    CLI->>Orch: execute_steps([1,2])

    loop 各ステップ
        Orch->>Scenario: execute_step(step_id)
        Scenario->>Factory: create_devices_batch(type, count)
        Factory-->>Scenario: デバイスリスト

        loop 各デバイス
            Scenario->>Device: run_continuous(interval, duration)
            Device->>Device: メッセージ生成
            Device->>IoT: メッセージ送信
        end

        Scenario->>Scenario: モニタリング・メトリクス記録
        Scenario-->>Orch: ステップ結果
    end

    Orch->>Orch: 結果ファイル保存
    Orch-->>CLI: 実行サマリー
    CLI-->>User: 完了通知
```

### 2.2 デバイスライフサイクル

```mermaid
stateDiagram-v2
    [*] --> Created : DeviceFactory.create_device()
    Created --> Connecting : run_continuous()
    Connecting --> Connected : IoT Hub接続成功
    Connecting --> Error : 接続失敗
    Error --> Connecting : リトライ

    Connected --> Running : メッセージ送信開始
    Running --> Running : 定期メッセージ送信
    Running --> Stopping : duration終了 or キャンセル
    Running --> Error : 送信エラー

    Stopping --> Disconnected : disconnect()
    Disconnected --> [*]
    Error --> [*] : クリーンアップ
```

## 3. クラス構成図

### 3.1 コアモジュール設計

```mermaid
classDiagram
    class BaseTestConfig {
        +scenario_type: str
        +scenario_name: str
        +device_types: List[DeviceType]
        +azure_config: AzureConfig
        +thresholds: Thresholds
    }

    class DeviceScalingConfig {
        +steps: List[DeviceScalingStep]
        +message_interval_seconds: int
    }

    class MessageFrequencyConfig {
        +steps: List[MessageFrequencyStep]
        +devices_per_type: int
    }

    class DataSizeLoadConfig {
        +steps: List[DataSizeLoadStep]
        +devices_per_type: int
        +message_interval_seconds: int
    }

    BaseTestConfig <|-- DeviceScalingConfig
    BaseTestConfig <|-- MessageFrequencyConfig
    BaseTestConfig <|-- DataSizeLoadConfig

    class TestOrchestrator {
        +config: BaseTestConfig
        +device_factory: DeviceFactory
        +metrics_collector: MetricsCollector
        +execute_steps(steps: List[int])
        +cleanup()
    }

    class ConfigManager {
        +load_config(scenario, options): BaseTestConfig
        +validate_config(config): bool
        +substitute_environment_variables(config)
    }
```

### 3.2 デバイス階層構造

```mermaid
classDiagram
    class BaseDevice {
        <<abstract>>
        +device_id: str
        +device_type: str
        +connection_string: str
        +run_continuous(interval, duration)
        +send_message(message)
        +disconnect()
        #generate_message(point_count)*
    }

    class BacnetDevice {
        +generate_message(point_count): Dict
    }

    class HvacDevice {
        +generate_message(point_count): Dict
    }

    class EnvironmentalDevice {
        +generate_message(point_count): Dict
    }

    class ElectricDevice {
        +generate_message(point_count): Dict
    }

    class BehaviorDevice {
        +generate_message(point_count): Dict
    }

    BaseDevice <|-- BacnetDevice
    BaseDevice <|-- HvacDevice
    BaseDevice <|-- EnvironmentalDevice
    BaseDevice <|-- ElectricDevice
    BaseDevice <|-- BehaviorDevice

    class DeviceFactory {
        +device_connection_strings: Dict
        +create_device(type, id): BaseDevice
        +create_devices_batch(type, count): List[BaseDevice]
        +validate_device_connections(): Dict[str, bool]
    }

    DeviceFactory --> BaseDevice : creates
```

### 3.3 シナリオ実行フロー

```mermaid
flowchart TD
    Start([CLI開始]) --> LoadConfig[設定読み込み<br/>ConfigManager]
    LoadConfig --> ValidateConfig[設定検証]
    ValidateConfig --> InitOrchestrator[TestOrchestrator初期化]

    InitOrchestrator --> StartMetrics[Prometheusサーバー開始]
    StartMetrics --> SelectScenario{シナリオ選択}

    SelectScenario --> |device_scaling| DeviceScaling[DeviceScalingScenario]
    SelectScenario --> |message_frequency| MessageFreq[MessageFrequencyScenario]
    SelectScenario --> |data_size_load| DataSizeLoad[DataSizeLoadScenario]

    DeviceScaling --> ExecuteSteps[指定ステップ実行]
    MessageFreq --> ExecuteSteps
    DataSizeLoad --> ExecuteSteps

    ExecuteSteps --> CreateDevices[デバイス作成<br/>DeviceFactory]
    CreateDevices --> StartDeviceTasks[デバイスタスク開始<br/>async/await]
    StartDeviceTasks --> MonitorExecution[実行モニタリング<br/>30秒間隔チェック]
    MonitorExecution --> RecordMetrics[メトリクス記録<br/>Prometheus]
    RecordMetrics --> StopDevices[デバイス停止・切断]

    StopDevices --> MoreSteps{他のステップ?}
    MoreSteps --> |Yes| ExecuteSteps
    MoreSteps --> |No| SaveResults[結果ファイル保存<br/>JSON形式]

    SaveResults --> StopMetrics[メトリクスサーバー停止]
    StopMetrics --> End([完了])
```

## 4. データフロー図

### 4.1 メッセージ生成・送信フロー

```mermaid
flowchart LR
    Templates[JSONテンプレート<br/>data/templates/] --> MsgGen[MessageGenerator]
    MsgGen --> |ランダム値注入| Message[生成メッセージ]

    Device[BaseDevice] --> |point_count指定| MsgGen
    Device --> |定期実行| SendLoop[送信ループ<br/>asyncio]

    Message --> Device
    SendLoop --> |Azure IoT SDK| IoTHub[Azure IoT Hub]

    IoTHub --> |成功/失敗| Metrics[MetricsCollector]
    Metrics --> |HTTP endpoint| Prometheus[Prometheus<br/>:8000/metrics]
    Metrics --> |集計結果| ResultFile[結果ファイル<br/>results/]
```

### 4.2 設定データフロー

```mermaid
flowchart TD
    EnvFile[.env ファイル] --> EnvVars[環境変数]
    ConfigFile[configs/scenarios/*.json] --> ConfigManager[ConfigManager]
    EnvVars --> ConfigManager

    ConfigManager --> |環境変数置換| ProcessedConfig[処理済み設定]
    ProcessedConfig --> |Pydantic検証| ValidatedConfig[検証済み設定]

    ValidatedConfig --> TestOrchestrator
    ValidatedConfig --> Scenarios[各シナリオクラス]
    ValidatedConfig --> DeviceFactory
```

## 5. コンポーネント責務

| コンポーネント       | 責務                                              |
| -------------------- | ------------------------------------------------- |
| **run_test.py**      | CLI インターフェース、引数解析、メイン実行制御    |
| **ConfigManager**    | 設定ファイル読み込み、環境変数置換、Pydantic 検証 |
| **TestOrchestrator** | テスト実行統括、シナリオ選択、結果集約            |
| **シナリオクラス**   | 各負荷パターンの実行ロジック、ステップ制御        |
| **DeviceFactory**    | デバイスインスタンス生成、接続文字列管理          |
| **BaseDevice**       | IoT Hub 接続、メッセージ送信、ライフサイクル管理  |
| **MessageGenerator** | デバイス別メッセージ生成、テンプレート処理        |
| **MetricsCollector** | Prometheus メトリクス収集、HTTP Server 管理       |

## 6. 非同期処理設計

### 6.1 並行実行パターン

```mermaid
graph TB
    MainThread[メインスレッド<br/>TestOrchestrator] --> AsyncLoop[asyncio イベントループ]

    AsyncLoop --> Step1[Step 1実行]
    AsyncLoop --> Step2[Step 2実行]
    AsyncLoop --> StepN[Step N実行]

    Step1 --> DeviceTasks1[デバイスタスク群1<br/>async Task]
    Step2 --> DeviceTasks2[デバイスタスク群2<br/>async Task]

    DeviceTasks1 --> Device1A[Device 1A<br/>run_continuous]
    DeviceTasks1 --> Device1B[Device 1B<br/>run_continuous]
    DeviceTasks1 --> Device1N[Device 1N<br/>run_continuous]

    Device1A --> |並行送信| IoTHub[Azure IoT Hub]
    Device1B --> |並行送信| IoTHub
    Device1N --> |並行送信| IoTHub

    MetricsThread[メトリクススレッド<br/>Prometheus HTTP Server] --> |並行実行| AsyncLoop
```

### 6.2 リソース管理

```mermaid
stateDiagram-v2
    [*] --> Init : TestOrchestrator作成
    Init --> StartMetrics : Prometheusサーバー開始
    StartMetrics --> ExecuteStep : ステップ実行開始

    state ExecuteStep {
        [*] --> CreateDevices : デバイス作成
        CreateDevices --> StartTasks : asyncタスク開始
        StartTasks --> Monitor : 実行モニタリング
        Monitor --> StopTasks : タスク停止
        StopTasks --> CleanupDevices : デバイス切断
        CleanupDevices --> [*]
    }

    ExecuteStep --> NextStep : 次ステップへ
    NextStep --> ExecuteStep : ステップ継続
    ExecuteStep --> Cleanup : 全ステップ完了

    Cleanup --> StopMetrics : メトリクスサーバー停止
    StopMetrics --> SaveResults : 結果保存
    SaveResults --> [*]
```

## 7. 設定システム設計

### 7.1 設定階層構造

```
configs/
├── default.env                    # 環境変数テンプレート
├── scenarios/                     # シナリオ別設定
│   ├── device_scaling.json       # デバイススケーリング設定
│   ├── message_frequency.json    # メッセージ頻度設定
│   └── data_size_load.json       # データサイズ負荷設定
└── custom/                       # カスタム設定（ユーザー作成）
```

### 7.2 設定マージフロー

```mermaid
flowchart TD
    DefaultConfig[デフォルト設定<br/>scenarios/*.json] --> EnvSubst[環境変数置換<br/>${VAR_NAME}]
    EnvFile[.env] --> EnvSubst

    EnvSubst --> CLIOverride[CLI引数オーバーライド<br/>--device-types, --duration]
    CLIOverride --> JSONOverride[JSON文字列オーバーライド<br/>--config-override]

    JSONOverride --> PydanticValidation[Pydantic型検証]
    PydanticValidation --> FinalConfig[最終設定オブジェクト]
```

## 8. メトリクスシステム設計

### 8.1 メトリクス種別

```mermaid
graph LR
    Metrics[MetricsCollector] --> Counter[Counter系]
    Metrics --> Histogram[Histogram系]
    Metrics --> Gauge[Gauge系]

    Counter --> MsgSent[messages_sent_total<br/>送信成功数]
    Counter --> MsgFailed[messages_failed_total<br/>送信失敗数]
    Counter --> ConnSuccess[connections_successful_total<br/>接続成功数]
    Counter --> ConnFailed[connections_failed_total<br/>接続失敗数]

    Histogram --> SendTime[message_send_duration_seconds<br/>送信時間分布]
    Histogram --> ConnTime[connection_duration_seconds<br/>接続時間分布]

    Gauge --> ActiveDevices[active_devices_gauge<br/>アクティブデバイス数]
    Gauge --> ActiveConns[active_connections_gauge<br/>アクティブ接続数]
```

### 8.2 メトリクス収集フロー

```mermaid
sequenceDiagram
    participant Device
    participant Metrics as MetricsCollector
    participant Prometheus
    participant ResultFile

    Device->>Metrics: record_message_sent(device_type, scenario)
    Device->>Metrics: record_message_failed(device_type, error)
    Device->>Metrics: record_connection_time(duration)

    Metrics->>Metrics: Prometheusメトリクス更新
    Metrics->>Prometheus: HTTPエンドポイント公開 (:8000)

    Note over Metrics: 定期的に集計
    Metrics->>Metrics: calculate_step_summary()
    Metrics->>ResultFile: export_metrics(JSON)
```

## 9. エラーハンドリング設計

### 9.1 エラー分類・対応

```mermaid
graph TD
    Error[エラー発生] --> ConfigError{設定エラー?}
    Error --> ConnectionError{接続エラー?}
    Error --> MessageError{メッセージエラー?}
    Error --> SystemError{システムエラー?}

    ConfigError --> |Yes| LogError[ログ出力] --> Exit[異常終了]
    ConnectionError --> |Yes| Retry[リトライ実行] --> MaxRetry{最大回数?}
    MaxRetry --> |Yes| LogError
    MaxRetry --> |No| Retry

    MessageError --> |Yes| Skip[スキップして継続] --> Continue[処理継続]
    SystemError --> |Yes| Cleanup[リソースクリーンアップ] --> LogError

    Continue --> Monitor[状況監視]
    Monitor --> ThresholdCheck{閾値超過?}
    ThresholdCheck --> |Yes| Cleanup
    ThresholdCheck --> |No| Continue
```

## 10. パフォーマンス考慮事項

### 10.1 スケーラビリティ設計

- **非同期処理**: asyncio による並行デバイス実行
- **メモリ効率**: デバイスプール管理、ステップ毎のリソース解放
- **スロットリング**: asyncio-throttle による送信レート制御
- **メトリクス最適化**: Prometheus pull 型メトリクス、HTTP Server 分離

### 10.2 負荷分散

```
Step 1: 100 devices  → 各タイプ 34 devices
Step 2: 500 devices  → 各タイプ 125 devices
Step 3: 1000 devices → 各タイプ 200 devices
Step 4: 2000 devices → 各タイプ 400 devices
```

各ステップは独立実行され、前ステップのリソースを完全にクリーンアップしてから次ステップを開始。
