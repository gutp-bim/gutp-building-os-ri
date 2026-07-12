import {
  ChevronDownIcon,
  ChevronUpDownIcon,
  ChevronUpIcon,
  FunnelIcon,
} from "@heroicons/react/24/outline";

type TableHeaderProps<T extends string> = {
  field: T;
  label: string;
  isFiltered: boolean;
  /** Retained for API compatibility; controls are always visible now (#118). */
  isHovered: boolean;
  isSorted: boolean;
  sortField: T | null;
  sortDirection: "asc" | "desc" | null;
  onSort: (field: T) => void;
  onFilterClick: (field: T) => void;
  onMouseEnter: (field: T) => void;
  onMouseLeave: () => void;
  filterButtonRef: (el: HTMLButtonElement | null) => void;
};

export function TableHeader<T extends string>({
  field,
  label,
  isFiltered,
  sortField,
  sortDirection,
  onSort,
  onFilterClick,
  onMouseEnter,
  onMouseLeave,
  filterButtonRef,
}: TableHeaderProps<T>) {
  return (
    <th
      className="px-6 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider relative w-[200px]"
      onMouseEnter={() => onMouseEnter(field)}
      onMouseLeave={onMouseLeave}
    >
      <div className="flex items-center justify-between">
        <span className={isFiltered ? "text-blue-600" : ""}>{label}</span>
        <div className="flex items-center space-x-1">
          <button
            onClick={() => onSort(field)}
            aria-label={`${label}で並べ替え`}
            className="text-gray-500 hover:text-gray-700"
          >
            {sortField === field
              ? (
                sortDirection === "asc"
                  ? <ChevronUpIcon className="w-5 h-5" />
                  : <ChevronDownIcon className="w-5 h-5" />
              )
              : <ChevronUpDownIcon className="w-5 h-5" />}
          </button>
          <button
            ref={filterButtonRef}
            onClick={() => onFilterClick(field)}
            aria-label={`${label}で絞り込み`}
            className={`${isFiltered ? "text-blue-600" : "text-gray-500"} hover:text-gray-700`}
          >
            <FunnelIcon className="w-5 h-5" />
          </button>
        </div>
      </div>
    </th>
  );
}
