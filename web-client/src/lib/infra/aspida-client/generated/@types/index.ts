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

export type Device = {
  dtId: string;
  id: string;
  name: string;
  buildingName?: string | null | undefined;
  floorNumber?: number | null | undefined;
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

/** Admin view of one gateway: binding + masked settings + pointlist sync status (#323). */
export type GatewayAdminView = {
  gatewayId?: string | undefined;
  bindingType?: string | undefined;

  settings?: {
    [key: string]: string;
  } | undefined;

  pointCount?: number | undefined;
  revision?: string | undefined;
  certTrustAnchor?: string | undefined;
}

export type GatewayCollision = {
  gatewayId?: string | undefined;
  buildingCount?: number | undefined;
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

export type MyResourcesResponse = {
  isAdmin?: boolean | undefined;

  resources?: {
    [key: string]: string[];
  } | null | undefined;
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
  panel?: string | null | undefined;
  labels?: string | null | undefined;
  scale?: number | null | undefined;
  installationArea?: string | null | undefined;
  unit?: string | null | undefined;
  interval?: number | null | undefined;
  instanceNoBacnet?: number | null | undefined;
  objectTypeBacnet?: string | null | undefined;
  deviceIdBacnet?: string | null | undefined;
  rowDataString?: string | null | undefined;

  identifiers?: {
    [key: string]: string;
  } | undefined;

  customTags?: {
    [key: string]: boolean;
  } | undefined;
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

export type TwinAdminControllerSparqlQueryRequest = {
  query?: string | undefined;
  maxRows?: number | null | undefined;
}

export type TwinAdminControllerTwinImportRequest = {
  turtle?: string | undefined;
  /** "append" (default) or "replace". */
  mode?: string | null | undefined;
}

export type TwinImportPreview = {
  tripleCount?: number | undefined;
  gatewayCount?: number | undefined;
  collisions?: GatewayCollision[] | undefined;
  valid?: boolean | undefined;
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

export type ValidTelemetryData = {
  building?: string | null | undefined;
  data?: string | null | undefined;
  datetime?: string | null | undefined;
  deviceId?: string | null | undefined;
  id?: string | null | undefined;
  name?: string | null | undefined;
  pointId?: string | null | undefined;
  value?: number | null | undefined;
}
