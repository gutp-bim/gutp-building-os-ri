/* eslint-disable */
/** API レスポンス DTO。監査ドメイン型をそのまま露出せず、result 文字列化して返す。 */
export type AdminAuditResponse = {
  id?: string | undefined;
  subjectType?: string | undefined;
  action?: string | undefined;
  targetId?: string | null | undefined;
  actorSub?: string | undefined;
  actorName?: string | null | undefined;
  result?: string | undefined;
  detail?: string | null | undefined;
  createdAt?: string | undefined;
}

export type AssistantChatRequest = {
  messages?: ChatMessage[] | undefined;
  context?: AssistantHelpContext | undefined;
}

export type AssistantContextTerm = {
  term?: string | undefined;
  definition?: string | undefined;
}

export type AssistantControllerAssistantChatResponse = {
  reply?: string | undefined;
}

export type AssistantHelpContext = {
  title?: string | null | undefined;
  body?: string[] | undefined;
  terms?: AssistantContextTerm[] | undefined;
}

/** Request body for `POST /telemetries/query/batch-latest` (#182). */
export type BatchLatestRequest = {
  pointIds?: string[] | undefined;
}

export type Building = {
  dtId: string;
  id: string;
  name: string;

  identifiers?: {
    [key: string]: string;
  } | undefined;

  customTags?: {
    [key: string]: boolean;
  } | undefined;
}

export type ChatMessage = {
  role?: string | undefined;
  content?: string | undefined;
}

export type ConfigEntry = {
  key?: string | undefined;
  isSecret?: boolean | undefined;
  isSet?: boolean | undefined;
  value?: string | null | undefined;
}

export type ControlSchema = {
  dataType?: string | undefined;
  enumLabels?: string | null | undefined;
  minValue?: number | null | undefined;
  maxValue?: number | null | undefined;
}

export type ControlSchemaDto = {
  dataType?: string | null | undefined;
  minValue?: string | null | undefined;
  maxValue?: string | null | undefined;
  enumLabels?: string | null | undefined;
}

export type Device = {
  dtId: string;
  id: string;
  name: string;
  buildingName?: string | null | undefined;
  owner?: string | null | undefined;
  site?: string | null | undefined;
  supplier?: string | null | undefined;
  gatewayId?: string | null | undefined;
  deviceType?: string | null | undefined;

  identifiers?: {
    [key: string]: string;
  } | undefined;

  customTags?: {
    [key: string]: boolean;
  } | undefined;
}

export type DeviceDetail = {
  device: Device;
  floor?: Floor | undefined;
  space?: Space | undefined;
}

export type DeviceRefDto = {
  dtId?: string | null | undefined;
  id?: string | null | undefined;
  name?: string | null | undefined;
}

export type EffectiveConfig = {
  entries?: ConfigEntry[] | undefined;
}

export type Floor = {
  dtId: string;
  id: string;
  name: string;

  identifiers?: {
    [key: string]: string;
  } | undefined;

  customTags?: {
    [key: string]: boolean;
  } | undefined;
}

/**
 * Admin view of one gateway: binding + masked settings + pointlist sync status (#323), a derived
 * BuildingOs.ApiServer.Controllers.GatewayAdminView.LastTelemetryAt last-seen signal (#181 Phase 2), the live egress
 * BuildingOs.ApiServer.Controllers.GatewayAdminView.Connected state (#230 Phase 2②), and the pointlist BuildingOs.ApiServer.Controllers.GatewayAdminView.PointlistSynced state
 * (#230 Phase 2b). `LastTelemetryAt` is the most recent telemetry timestamp across the gateway's
 * points (ISO-8601), or `null` when none have reported — it is the ingress last-seen, distinct
 * from `Connected`. `Connected` is the cross-replica egress heartbeat (ADR-0004): `true`
 * when a bridge replica is holding a live egress stream for this gateway right now, `false` when
 * none is observed (TTL-expired/absent). `PointlistSynced` compares the ETag the gateway reports
 * as applied against the twin-authoritative BuildingOs.ApiServer.Controllers.GatewayAdminView.Revision: `true` = in sync, `false`
 * = drifted (a resync is warranted), `null` = the gateway has not reported one (unknown — e.g. not
 * connected, or a gateway build that predates the report).
 */
export type GatewayAdminView = {
  gatewayId?: string | undefined;
  bindingType?: string | undefined;

  settings?: {
    [key: string]: string;
  } | undefined;

  pointCount?: number | undefined;
  revision?: string | undefined;
  certTrustAnchor?: string | undefined;
  lastTelemetryAt?: string | null | undefined;
  connected?: boolean | undefined;
  pointlistSynced?: boolean | null | undefined;
}

export type GatewayCollision = {
  gatewayId?: string | undefined;
  buildingCount?: number | undefined;
}

export type GatewayPointDto = {
  pointId?: string | undefined;
  localId?: string | null | undefined;
  protocol?: string | null | undefined;
  native?: NativeAddressingDto | undefined;
  unit?: string | null | undefined;
  writable?: boolean | null | undefined;
  controlSchema?: ControlSchemaDto | undefined;
  device?: DeviceRefDto | undefined;
}

/** Gateway point-list export response (#224). BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListResponse.Revision equals the ETag. */
export type GatewayPointListResponse = {
  gatewayId?: string | undefined;
  revision?: string | undefined;
  generatedAt?: string | undefined;
  points?: GatewayPointDto[] | undefined;
}

export type GroupsControllerAddResourceRequest = {
  resourceType?: string | undefined;
  resourceId?: string | undefined;
}

export type GroupsControllerBulkAddResourceRequest = {
  items?: GroupsControllerAddResourceRequest[] | undefined;
}

export type GroupsControllerBulkAddResourceResponse = {
  added?: GroupsControllerResourceItemResponse[] | undefined;
  failed?: string[] | undefined;
}

export type GroupsControllerCreateGroupRequest = {
  id?: string | undefined;
  name?: string | undefined;
  description?: string | null | undefined;
}

export type GroupsControllerGroupDetailResponse = {
  id?: string | undefined;
  name?: string | undefined;
  description?: string | null | undefined;
  createdAt?: string | undefined;
  updatedAt?: string | undefined;
  resourceItems?: GroupsControllerResourceItemResponse[] | undefined;
}

export type GroupsControllerGroupResponse = {
  id?: string | undefined;
  name?: string | undefined;
  description?: string | null | undefined;
  createdAt?: string | undefined;
  updatedAt?: string | undefined;
}

export type GroupsControllerResourceItemResponse = {
  id?: string | undefined;
  resourceType?: string | undefined;
  resourceId?: string | undefined;
  createdAt?: string | undefined;
}

export type GroupsControllerUpdateGroupRequest = {
  name?: string | null | undefined;
  description?: string | null | undefined;
}

/**
 * One point's latest sample; `Datetime`/`Value` are null when it has no data (#182).
 * 
 * `Value` is the union-typed reading (#344) — a number, string, or boolean — described in the
 * OpenAPI document as `oneOf` by `TelemetryValueSchemaFilter`. `State` carries the
 * reading's non-numeric half, and `ValueType` describes `Value`; the legacy
 * `ValueText`/`ValueBool` pair left the wire in #359. Kept in step with
 * BuildingOs.ApiServer.Telemetry.TelemetryReading, whose docs carry the full rationale
 * — the client's value decoder is satisfied structurally by both, so a divergence here surfaces only
 * as a type error in the generated client.
 * Response-only: `object?` deserializes as a `JsonElement`, so do not reuse this for input.
 */
export type LatestSample = {
  /** The point this sample belongs to. */
  pointId?: string | undefined;
  /** ISO-8601 timestamp of the reading; `null` when the point has no data. */
  datetime?: string | null | undefined;

  /**
   * The reading, as a System.Double, System.String, System.Boolean, or `null`.
   * Widened to `oneOf: [number, string, boolean]` in the OpenAPI document by
   * `TelemetryValueSchemaFilter`, which is what makes generated clients see a real union rather
   * than an untyped hole.
   */
  value?: number | null | string | boolean | undefined;

  /**
   * `"number"` | `"string"` | `"boolean"` — the kind of Value,
   *             derived from the value actually shipped rather than copied from the stored tag, so it cannot
   *             contradict it. A descriptor, not a lookup key.
   */
  valueType?: string | null | undefined;

  /**
   * The reading's <b>non-numeric half</b> — a System.String, a System.Boolean, or
   * `null` — independent of any number in Value (#359). Replaced the legacy
   * `ValueText`/`ValueBool` pair. A non-numeric reading is repeated here rather than left
   * null, so a client reads the state half with a single lookup instead of falling back to
   * Value. Batch-latest returns raw samples only, so unlike
   * BuildingOs.ApiServer.Telemetry.TelemetryReading.State this never carries a state
   * alongside a numeric average — see that type's docs for why the field exists at all.
   */
  state?: string | null | boolean | undefined;
}

export type MyResourcesResponse = {
  isAdmin?: boolean | undefined;

  resources?: {
    [key: string]: string[];
  } | null | undefined;
}

export type NativeAddressingDto = {
  protocol?: string | undefined;
  deviceId?: string | null | undefined;
  objectType?: string | null | undefined;
  instanceNo?: string | null | undefined;
}

export type OidcClientDetail = {
  id?: string | undefined;
  clientId?: string | undefined;
  enabled?: boolean | undefined;
  serviceAccountsEnabled?: boolean | undefined;
  publicClient?: boolean | undefined;
  description?: string | null | undefined;
  redirectUris?: string[] | undefined;
}

export type OidcClientSummary = {
  id?: string | undefined;
  clientId?: string | undefined;
  enabled?: boolean | undefined;
  serviceAccountsEnabled?: boolean | undefined;
  description?: string | null | undefined;
}

export type OidcClientsControllerCreateOidcClientRequest = {
  clientId?: string | undefined;
  description?: string | null | undefined;
  serviceAccountsEnabled?: boolean | undefined;
  redirectUris?: string[] | null | undefined;
}

/** Create response — carries the one-time plaintext secret (never returned again). */
export type OidcClientsControllerCreatedOidcClientResponse = {
  client?: OidcClientDetail | undefined;
  secret?: string | undefined;
}

export type OidcClientsControllerRotatedSecretResponse = {
  secret?: string | undefined;
}

export type OidcClientsControllerSetEnabledRequest = {
  enabled?: boolean | undefined;
}

export type PermissionsControllerResolvedPermissionInfo = {
  originalId?: string | undefined;
  resourceType?: string | undefined;
  displayName?: string | null | undefined;
}

export type Point = {
  dtId: string;
  id: string;
  name: string;
  specification?: string | null | undefined;
  type?: string | null | undefined;
  writable?: boolean | null | undefined;
  gatewayName?: string | null | undefined;
  minPresValue?: number | null | undefined;
  maxPresValue?: number | null | undefined;
  targetArea?: string | null | undefined;
  scale?: number | null | undefined;
  installationArea?: string | null | undefined;
  unit?: string | null | undefined;
  interval?: number | null | undefined;
  alarmHigh?: number | null | undefined;
  alarmLow?: number | null | undefined;
  warnHigh?: number | null | undefined;
  warnLow?: number | null | undefined;
  instanceNoBacnet?: number | null | undefined;
  objectTypeBacnet?: string | null | undefined;
  deviceIdBacnet?: string | null | undefined;

  identifiers?: {
    [key: string]: string;
  } | undefined;

  customTags?: {
    [key: string]: boolean;
  } | undefined;
}

/**
 * 制御監査履歴の API レスポンス DTO（#162）。`Result` の生 JSON はそのまま露出せず、`Status`
 * （"success" / "failed" / "pending"）に正規化して返す。`Request` は送信時のコマンド JSON。
 */
export type PointControlAuditResponse = {
  controlId?: string | undefined;
  pointId?: string | null | undefined;
  request?: string | undefined;
  status?: string | undefined;
  createdAt?: string | undefined;
  completedAt?: string | null | undefined;
}

export type PointControllerControlAcceptedResponse = {
  controlId: string;
}

export type PointControllerPointControlRequest = {
  value?: number | null | undefined;
}

export type PointDetail = {
  point: Point;
  floor?: Floor | undefined;
  space?: Space | undefined;
  device?: Device | undefined;
  controlSchema?: ControlSchema | undefined;
}

export type ProblemDetails = {
  type?: string | null | undefined;
  title?: string | null | undefined;
  status?: number | null | undefined;
  detail?: string | null | undefined;
  instance?: string | null | undefined;
}

export type ResourceMetadataPatchRequest = {
  identifiers?: {
    [key: string]: string | null;
  } | null | undefined;

  customTags?: {
    [key: string]: boolean | null;
  } | null | undefined;
}

export type ResourceMetadataResponse = {
  identifiers?: {
    [key: string]: string;
  } | undefined;

  customTags?: {
    [key: string]: boolean;
  } | undefined;
}

export type ResourceSearchHit = {
  type: string;
  dtId: string;
  id: string;
  name: string;
  buildingDtId?: string | null | undefined;
}

export type RoleCatalogEntry = {
  role?: string | undefined;
  isAdmin?: boolean | undefined;
  workspaces?: string[] | undefined;
  description?: string | undefined;
}

export type ServiceStatus = {
  name?: string | undefined;
  status?: string | undefined;
}

export type SettingSource = 'Default' | 'Ui'

export type SettingType = 'Boolean' | 'Number' | 'String'

export type SettingView = {
  key?: string | undefined;
  type?: SettingType | undefined;
  description?: string | undefined;
  category?: string | undefined;
  value?: string | undefined;
  defaultValue?: string | undefined;
  isOverridden?: boolean | undefined;
  source?: SettingSource | undefined;
  updatedAt?: string | null | undefined;
  updatedBy?: string | null | undefined;
}

export type Space = {
  dtId: string;
  id: string;
  name: string;

  identifiers?: {
    [key: string]: string;
  } | undefined;

  customTags?: {
    [key: string]: boolean;
  } | undefined;
}

export type SparqlQueryResult = {
  columns?: string[] | undefined;
  rows?: {
    [key: string]: string;
  }[] | undefined;
  rowCount?: number | undefined;
  truncated?: boolean | undefined;
  elapsedMs?: number | undefined;
}

export type SystemConfigControllerUpdateSettingRequest = {
  value?: string | null | undefined;
}

export type SystemKpis = {
  msgRate1m?: number | null | undefined;
  controlReq5m?: number | null | undefined;
}

export type SystemStatus = {
  services?: ServiceStatus[] | undefined;
  kpis?: SystemKpis | undefined;
  metricsAvailable?: boolean | undefined;
}

export type TelemetryGranularity = 0 | 1 | 2

/**
 * Response-only wire DTO for telemetry reads (#344).
 *             
 * 
 * Until now the controllers returned BuildingOS.Shared.ValidTelemetryData directly, so the storage
 * layer's discriminated split (`value`/`valueType`/`valueText`/`valueBool`,
 * #152) leaked into the HTTP contract and every API consumer had to reassemble it. That split is a
 * Parquet/EF concern — BuildingOS.Shared.ValidTelemetryData is an EF entity and binds the lake's column
 * model, so it cannot be retyped in place — while the canonical schema
 * (`Defines/Schemas/valid-message.json`) and the NATS bus have always carried one polymorphic
 * `value`. This DTO restores that shape at the boundary.
 * <b>#359 removed the legacy payload fields.</b>`valueText`/`valueBool` are gone; the
 * non-numeric half of a reading now travels in BuildingOs.ApiServer.Telemetry.TelemetryReading.State. BuildingOs.ApiServer.Telemetry.TelemetryReading.ValueType stays,
 * because it never duplicated the payload the way those two did — it describes BuildingOs.ApiServer.Telemetry.TelemetryReading.Value,
 * derived from the value actually shipped rather than copied from the stored tag. (Copying the
 * stored tag is what made the wire say `{ value: 42, valueType: "string" }` for a mixed
 * aggregate bucket, where the stored tag classifies the bucket's last-in-bucket reading.)
 */
export type TelemetryReading = {
  pointId?: string | null | undefined;
  datetime?: string | null | undefined;

  /**
   * The reading, as a System.Double, System.String, System.Boolean, or `null`.
   * Declared `object?` so System.Text.Json writes it by its runtime type; the OpenAPI schema is
   * widened to `oneOf: [number, string, boolean]` by `TelemetryValueSchemaFilter`, which is
   * what makes the generated clients see a real union rather than an untyped hole.
   * 
   * <b>Response-only.</b> Round-tripping this record through a request body would deserialize
   * `Value` as a `JsonElement`, not the original primitive. Do not reuse it for input or as
   * a NATS payload without adding a converter.
   */
  value?: number | null | string | boolean | undefined;

  building?: string | null | undefined;
  deviceId?: string | null | undefined;
  name?: string | null | undefined;
  data?: string | null | undefined;
  id?: string | null | undefined;
  valueType?: string | null | undefined;

  /**
   * The reading's <b>non-numeric half</b> — a System.String, a System.Boolean, or
   * `null` — independent of any number in Value (#359).
   * 
   * It exists for the one row shape that carries two readings at once: an aggregate bucket sets
   * Value to the average unconditionally (the continuous-aggregate contract), so a
   * mixed hour ships the number while its last-in-bucket state has nowhere else to live. That is what
   * the state timeline reads at Hour/Day granularity.
   * A raw non-numeric row <b>repeats</b> its reading here rather than leaving this null. The
   * duplication is deliberate: it makes this field mean one thing unconditionally, so a client reads
   * it with a single lookup instead of falling back to Value — the fallback chain
   * #359 exists to delete. Widened to `oneOf: [string, boolean]` by
   * `TelemetryValueSchemaFilter`, for the same reason Value is.
   */
  state?: string | null | boolean | undefined;
}

export type TelemetryThresholds = {
  staleThresholdSeconds?: number | undefined;
  staleIntervalMultiplier?: number | undefined;
}

export type TwinAdminControllerSparqlQueryRequest = {
  query?: string | undefined;
  maxRows?: number | null | undefined;
}

export type TwinAdminControllerTwinImportRequest = {
  turtle?: string | undefined;
  /** "append" (default) or "replace". プレビューでも使う（階層未接続の判定範囲、#291）。 */
  mode?: string | null | undefined;
  /**
   * 階層未接続リソース（#291）があっても適用する明示的な上書き。既定 false（拒否）。
   * gateway_id 一意性違反は上書きできない。
   */
  allowOrphans?: boolean | undefined;
}

export type TwinImportPreview = {
  tripleCount?: number | undefined;
  gatewayCount?: number | undefined;
  collisions?: GatewayCollision[] | undefined;
  orphanCount?: number | undefined;
  orphans?: TwinOrphanResource[] | undefined;
  valid?: boolean | undefined;
}

export type TwinOrphanResource = {
  resourceId?: string | undefined;
  reason?: string | undefined;
}

export type UsersControllerAddPermissionRequest = {
  permission?: string | undefined;
}

export type UsersControllerRemovePermissionRequest = {
  permission?: string | undefined;
}

export type UsersControllerSetEnabledRequest = {
  enabled?: boolean | undefined;
}

export type UsersControllerUpdateUserAttributesApiRequest = {
  role?: string | null | undefined;
  permissions?: string[] | null | undefined;

  /** リソースIDに対応する表示名のマップ（キー: 元のリソースID、値: 表示名） */
  resourceDisplayNames?: {
    [key: string]: string;
  } | null | undefined;
}

export type UsersControllerUserResponse = {
  id?: string | undefined;
  displayName?: string | undefined;
  email?: string | null | undefined;
  userPrincipalName?: string | null | undefined;
  role?: string | null | undefined;
  permissions?: string[] | undefined;
  enabled?: boolean | undefined;
}
