import { listBuildings, listChildren } from "./repository";
import type { ResourceRef } from "./types";

/** Injectable data source for {@link ResourceTreeView}: roots + one level of children on expand. */
export type ResourceTreeLoaders = {
  loadRoots: (signal?: AbortSignal) => Promise<ResourceRef[]>;
  loadChildren: (
    parent: ResourceRef,
    signal?: AbortSignal,
  ) => Promise<ResourceRef[]>;
};

/** The production loaders, backed by the resource repository façade. */
export const defaultTreeLoaders: ResourceTreeLoaders = {
  loadRoots: () => listBuildings(),
  loadChildren: (parent) => listChildren(parent),
};
