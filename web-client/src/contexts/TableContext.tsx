"use client";

import { FilterState, SortDirection } from "@/types/device-table";
import { createContext, ReactNode, useContext, useReducer } from "react";

type TableState<T extends string = string> = {
  currentPage: number;
  pageSize: number;
  sortField: T | null;
  sortDirection: SortDirection;
  hoveredField: T | null;
  activeFilter: T | null;
  filterPosition: { top: number; left: number };
  filters: FilterState<T>;
};

type TableAction<T extends string = string> =
  | { type: "SET_PAGE"; payload: number }
  | { type: "SET_PAGE_SIZE"; payload: number }
  | {
    type: "SET_SORT";
    payload: { field: T; direction: SortDirection };
  }
  | { type: "CLEAR_SORT" }
  | { type: "SET_HOVERED_FIELD"; payload: T | null }
  | {
    type: "SET_ACTIVE_FILTER";
    payload: {
      field: T | null;
      position?: { top: number; left: number };
    };
  }
  | { type: "UPDATE_FILTERS"; payload: { field: T; items: string[] } };

const createInitialState = <T extends string>(fields: T[]): TableState<T> => ({
  currentPage: 1,
  pageSize: 10,
  sortField: null,
  sortDirection: null,
  hoveredField: null,
  activeFilter: null,
  filterPosition: { top: 0, left: 0 },
  filters: fields.reduce(
    (acc, field) => ({ ...acc, [field]: [] }),
    {} as FilterState<T>,
  ),
});

const TableContext = createContext<
  {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    state: TableState<any>;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    dispatch: React.Dispatch<TableAction<any>>;
  } | null
>(null);

function tableReducer<T extends string>(
  state: TableState<T>,
  action: TableAction<T>,
): TableState<T> {
  switch (action.type) {
    case "SET_PAGE":
      return { ...state, currentPage: action.payload };
    case "SET_PAGE_SIZE":
      return { ...state, pageSize: action.payload, currentPage: 1 };
    case "SET_SORT":
      return {
        ...state,
        sortField: action.payload.field,
        sortDirection: action.payload.direction,
      };
    case "CLEAR_SORT":
      return { ...state, sortField: null, sortDirection: null };
    case "SET_HOVERED_FIELD":
      return { ...state, hoveredField: action.payload };
    case "SET_ACTIVE_FILTER":
      return {
        ...state,
        activeFilter: action.payload.field,
        filterPosition: action.payload.position || state.filterPosition,
      };
    case "UPDATE_FILTERS":
      return {
        ...state,
        filters: {
          ...state.filters,
          [action.payload.field]: action.payload.items,
        },
        currentPage: 1,
      };
    default:
      return state;
  }
}

export function TableProvider<T extends string>({
  children,
  fields,
}: {
  children: ReactNode;
  fields: T[];
}) {
  const [state, dispatch] = useReducer(
    (state: TableState<T>, action: TableAction<T>) =>
      tableReducer(state, action),
    createInitialState(fields),
  );

  return (
    <TableContext.Provider value={{ state, dispatch }}>
      {children}
    </TableContext.Provider>
  );
}

export function useTable<T extends string>() {
  const context = useContext(TableContext);
  if (!context) {
    throw new Error("useTable must be used within a TableProvider");
  }
  return context as {
    state: TableState<T>;
    dispatch: React.Dispatch<TableAction<T>>;
  };
}
