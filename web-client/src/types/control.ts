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
