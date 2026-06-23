export type SortField<T extends string = string> = T;
export type SortDirection = "asc" | "desc" | null;

export type FilterState<T extends string = string> = {
  [key in T]: string[];
};
