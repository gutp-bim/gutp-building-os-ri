/**
 * Effective-config view types (#147), mirroring the API server's `EffectiveConfig` / `ConfigEntry`.
 * Secret entries never carry a value — only `isSet` reports presence.
 */
export interface ConfigEntry {
  key: string;
  isSecret: boolean;
  isSet: boolean;
  value?: string | null;
}

export interface EffectiveConfig {
  entries: ConfigEntry[];
}
