/* eslint-disable */
export type AddPermissionRequest = {
  permission?: string | undefined;
}

export type AddResourceRequest = {
  resourceType?: string | undefined;
  resourceId?: string | undefined;
}

export type Building = {
  dtId: string;
  id: string;
  name: string;
}

export type ResourceSearchHit = {
  type: string;
  dtId: string;
  id: string;
  name: string;
  buildingDtId?: string | null | undefined;
}

export type BulkAddResourceRequest = {
  items?: AddResourceRequest[] | undefined;
}

export type BulkAddResourceResponse = {
  added?: ResourceItemResponse[] | undefined;
  failed?: string[] | undefined;
}

export type ControlSchema = {
  dataType?: string | undefined;
  enumLabels?: string | null | undefined;
}

export type CreateGroupRequest = {
  id?: string | undefined;
  name?: string | undefined;
  description?: string | null | undefined;
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
}

export type DeviceDetail = {
  device: Device;
  floor?: Floor | undefined;
  space?: Space | undefined;
}

export type Floor = {
  dtId: string;
  id: string;
  name: string;
}

export type GroupDetailResponse = {
  id?: string | undefined;
  name?: string | undefined;
  description?: string | null | undefined;
  createdAt?: string | undefined;
  updatedAt?: string | undefined;
  resourceItems?: ResourceItemResponse[] | undefined;
}

export type GroupResponse = {
  id?: string | undefined;
  name?: string | undefined;
  description?: string | null | undefined;
  createdAt?: string | undefined;
  updatedAt?: string | undefined;
}

export type MyResourcesResponse = {
  isAdmin?: boolean | undefined;

  resources?: {
    [key: string]: string[];
  } | null | undefined;
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
}

export type PointControlRequest = {
  controlType?: string | null | undefined;
  body?: string | null | undefined;
}

export type PointControlResponse = {
  response?: string | undefined;
  result?: PointControlResult | undefined;
}

export type PointControlResult = 0 | 1

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

export type RemovePermissionRequest = {
  permission?: string | undefined;
}

export type ResourceItemResponse = {
  id?: string | undefined;
  resourceType?: string | undefined;
  resourceId?: string | undefined;
  createdAt?: string | undefined;
}

export type Space = {
  dtId: string;
  id: string;
  name: string;
}

export type UpdateGroupRequest = {
  name?: string | null | undefined;
  description?: string | null | undefined;
}

export type UpdateUserAttributesApiRequest = {
  role?: string | null | undefined;
  permissions?: string[] | null | undefined;
}

export type UserResponse = {
  id?: string | undefined;
  displayName?: string | undefined;
  email?: string | null | undefined;
  userPrincipalName?: string | null | undefined;
  role?: string | null | undefined;
  permissions?: string[] | undefined;
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
