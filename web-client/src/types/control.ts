/**
 * 機器制御用の型定義
 */

/**
 * BACnet制御用のリクエストBody型
 */
export type BacnetControlRequestBody = {
  methodName: string;
  gatewayId: string;
  destDevId: string;
  objectType: string;
  objectInstanceNo: number;
  intValue?: number;
  boolValue?: boolean;
  priority?: number;
};

/**
 * DK Connect制御用のリクエストBody型
 * 注意: このオブジェクトはJSON.stringify()で文字列化され、
 *       PointControlRequestのbodyフィールドに格納される
 */
export type DkConnectControlRequestBody = {
  /** DK Connect APIのビルディングID（省略時はバックエンドで環境変数から取得） */
  buildingId?: string;
  /** 機器IDリスト（必須、空配列不可、最大200件） */
  equipmentIdList: string[];
  /** 制御パラメータ */
  operations: DkConnectOperations;
};

/**
 * DK Connect制御のパラメータ型
 */
export type DkConnectOperations = {
  /** 運転/停止: true: 運転, false: 停止 */
  onOff?: boolean;
  /** 運転モード: 1:冷房, 2:暖房, 3:送風, 4:自動, 5:ドライ, 6:除湿冷房 */
  operatingMode?: 1 | 2 | 3 | 4 | 5 | 6;
  /** 設定温度: -127.9 ~ 127.9℃（小数第二位を四捨五入） */
  setpoint?: number;
  /** 風量: 1:弱, 2:急, 3:強, 4:自動 */
  fanSpeed?: 1 | 2 | 3 | 4;
  /** 風向: 1-5:風向, 6:スイング */
  airDirection?: 1 | 2 | 3 | 4 | 5 | 6;
  /** フィルタサインリセット */
  filterSignReset?: boolean;
  /** リモコン運転禁止設定 */
  RCOnOffProhibition?: boolean;
  /** 換気モード: 1:普通, 2:全熱交, 3:自動 */
  ventilationMode?: 1 | 2 | 3;
  /** 換気量: 1:弱（通常）, 2:強（通常）, 3:自動（通常）, 4:弱（フレッシュアップ）, 5:強（フレッシュアップ）, 6:自動（フレッシュアップ） */
  ventilationFanSpeed?: 1 | 2 | 3 | 4 | 5 | 6;
  /** 室外機能力抑制状態: 40%, 70%, 100% */
  outdoorPowerRatio?: 40 | 70 | 100;
};
