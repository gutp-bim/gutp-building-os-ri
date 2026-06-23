import { makeApi, Zodios, type ZodiosOptions } from "@zodios/core";
import { z } from "zod";

const Building = z.object({ id: z.string().min(1), name: z.string().min(1) });
const ProblemDetails = z
  .object({
    type: z.string().nullable(),
    title: z.string().nullable(),
    status: z.number().int().nullable(),
    detail: z.string().nullable(),
    instance: z.string().nullable(),
  })
  .partial()
  .passthrough();
const Device = z.object({
  id: z.string().min(1),
  name: z.string().min(1),
  buildingName: z.string().nullish(),
  floorNumber: z.number().int().nullish(),
  owner: z.string().nullish(),
  site: z.string().nullish(),
  supplier: z.string().nullish(),
});
const Floor = z.object({ id: z.string().min(1), name: z.string().min(1) });
const Point = z.object({
  id: z.string().min(1),
  name: z.string().min(1),
  dataSpecification: z.string().nullish(),
  dataType: z.string().nullish(),
  gatewayName: z.string().nullish(),
  min: z.number().int().nullish(),
  max: z.number().int().nullish(),
  targetArea: z.string().nullish(),
  panel: z.string().nullish(),
  writable: z.boolean().nullish(),
});
const Space = z.object({ id: z.string().min(1), name: z.string().min(1) });

export const schemas = {
  Building,
  ProblemDetails,
  Device,
  Floor,
  Point,
  Space,
};

const endpoints = makeApi([
  {
    method: "get",
    path: "/buildings",
    alias: "getBuildings",
    requestFormat: "json",
    parameters: [
      {
        name: "page",
        type: "Query",
        schema: z.number().int().optional().default(1),
      },
      {
        name: "pageSize",
        type: "Query",
        schema: z.number().int().optional().default(10),
      },
    ],
    response: z.array(Building),
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/buildings/:buildingId",
    alias: "getBuildingsBuildingId",
    requestFormat: "json",
    parameters: [
      {
        name: "buildingId",
        type: "Path",
        schema: z.string(),
      },
    ],
    response: Building,
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
      {
        status: 404,
        description: `Not Found`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/devices",
    alias: "getDevices",
    requestFormat: "json",
    parameters: [
      {
        name: "spaceId",
        type: "Query",
        schema: z.string().optional(),
      },
      {
        name: "page",
        type: "Query",
        schema: z.number().int().optional().default(1),
      },
      {
        name: "pageSize",
        type: "Query",
        schema: z.number().int().optional().default(10),
      },
    ],
    response: z.array(Device),
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/devices/:deviceId",
    alias: "getDevicesDeviceId",
    requestFormat: "json",
    parameters: [
      {
        name: "deviceId",
        type: "Path",
        schema: z.string(),
      },
    ],
    response: Device,
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
      {
        status: 404,
        description: `Not Found`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "post",
    path: "/devices/command/:deviceId",
    alias: "postDevicescommandDeviceId",
    requestFormat: "json",
    parameters: [
      {
        name: "deviceId",
        type: "Path",
        schema: z.string(),
      },
      {
        name: "payload",
        type: "Query",
        schema: z.string().optional(),
      },
    ],
    response: z.string(),
    errors: [
      {
        status: 400,
        description: `Bad Request`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/fabric-access-test",
    alias: "getFabricAccessTest",
    requestFormat: "json",
    response: z.void(),
  },
  {
    method: "get",
    path: "/fabric-sql-test",
    alias: "getFabricSqlTest",
    requestFormat: "json",
    response: z.void(),
  },
  {
    method: "get",
    path: "/floors",
    alias: "getFloors",
    requestFormat: "json",
    parameters: [
      {
        name: "buildingId",
        type: "Query",
        schema: z.string().optional(),
      },
      {
        name: "page",
        type: "Query",
        schema: z.number().int().optional().default(1),
      },
      {
        name: "pageSize",
        type: "Query",
        schema: z.number().int().optional().default(10),
      },
    ],
    response: z.array(Floor),
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/floors/:floorId",
    alias: "getFloorsFloorId",
    requestFormat: "json",
    parameters: [
      {
        name: "floorId",
        type: "Path",
        schema: z.string(),
      },
    ],
    response: Floor,
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
      {
        status: 404,
        description: `Not Found`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/points",
    alias: "getPoints",
    requestFormat: "json",
    parameters: [
      {
        name: "deviceId",
        type: "Query",
        schema: z.string().optional(),
      },
    ],
    response: z.array(Point),
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/points/:pointId",
    alias: "getPointsPointId",
    requestFormat: "json",
    parameters: [
      {
        name: "pointId",
        type: "Path",
        schema: z.string(),
      },
    ],
    response: Point,
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
      {
        status: 404,
        description: `Not Found`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/spaces",
    alias: "getSpaces",
    requestFormat: "json",
    parameters: [
      {
        name: "floorId",
        type: "Query",
        schema: z.string().optional(),
      },
      {
        name: "page",
        type: "Query",
        schema: z.number().int().optional().default(1),
      },
      {
        name: "pageSize",
        type: "Query",
        schema: z.number().int().optional().default(10),
      },
    ],
    response: z.array(Space),
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/spaces/:spaceId",
    alias: "getSpacesSpaceId",
    requestFormat: "json",
    parameters: [
      {
        name: "spaceId",
        type: "Path",
        schema: z.string(),
      },
    ],
    response: Space,
    errors: [
      {
        status: 401,
        description: `Unauthorized`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
      {
        status: 404,
        description: `Not Found`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
  {
    method: "get",
    path: "/telemetries",
    alias: "getTelemetries",
    requestFormat: "json",
    parameters: [
      {
        name: "pointIds",
        type: "Query",
        schema: z.string().optional(),
      },
      {
        name: "startTime",
        type: "Query",
        schema: z.string().datetime({ offset: true }).optional(),
      },
      {
        name: "endTime",
        type: "Query",
        schema: z.string().datetime({ offset: true }).optional(),
      },
      {
        name: "count",
        type: "Query",
        schema: z.number().int().optional(),
      },
      {
        name: "dataStorageType",
        type: "Query",
        schema: z.union([z.literal(0), z.literal(1)]).optional(),
      },
    ],
    response: z.string(),
    errors: [
      {
        status: 400,
        description: `Bad Request`,
        schema: z
          .object({
            type: z.string().nullable(),
            title: z.string().nullable(),
            status: z.number().int().nullable(),
            detail: z.string().nullable(),
            instance: z.string().nullable(),
          })
          .partial()
          .passthrough(),
      },
    ],
  },
]);

export const api = new Zodios(endpoints);

export function createApiClient(baseUrl: string, options?: ZodiosOptions) {
  return new Zodios(baseUrl, endpoints, options);
}
