/**
 * Editable app-settings types (#148), mirroring the API server's `SettingView`. Enums serialize as
 * their .NET names (`JsonStringEnumConverter`).
 */
export type SettingType = "Boolean" | "Number" | "String";
export type SettingSource = "Default" | "Ui";

export interface SettingView {
  key: string;
  type: SettingType;
  description: string;
  category: string;
  value: string;
  defaultValue: string;
  isOverridden: boolean;
  source: SettingSource;
  updatedAt?: string | null;
  updatedBy?: string | null;
}
