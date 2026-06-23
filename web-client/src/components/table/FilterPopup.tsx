import { useEffect, useRef } from "react";
import { XMarkIcon } from "@heroicons/react/24/outline";

type FilterPopupProps<T extends string> = {
  field: T;
  items: string[];
  selectedItems: string[];
  onClose: () => void;
  onFilterChange: (field: T, selectedItems: string[]) => void;
  position: { top: number; left: number };
};

export const FilterPopup = <T extends string>({
  field,
  items,
  selectedItems,
  onClose,
  onFilterChange,
  position,
}: FilterPopupProps<T>) => {
  const popupRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (
        popupRef.current && !popupRef.current.contains(event.target as Node)
      ) {
        onClose();
      }
    };

    document.addEventListener("mousedown", handleClickOutside);
    return () => {
      document.removeEventListener("mousedown", handleClickOutside);
    };
  }, [onClose]);

  const handleCheckboxChange = (item: string) => {
    const newSelectedItems = selectedItems.includes(item)
      ? selectedItems.filter((i) => i !== item)
      : [...selectedItems, item];
    onFilterChange(field, newSelectedItems);
  };

  return (
    <div
      ref={popupRef}
      className="absolute z-50 bg-white border border-gray-200 rounded-lg shadow-lg"
      style={{
        top: `${position.top}px`,
        left: `${position.left}px`,
        minWidth: "200px",
        maxHeight: "300px",
        overflowY: "auto",
      }}
    >
      <div className="p-2 border-b border-gray-200 flex justify-between items-center">
        <span className="text-sm font-medium text-gray-700">フィルター</span>
        <button
          onClick={onClose}
          className="text-gray-400 hover:text-gray-600"
        >
          <XMarkIcon className="w-4 h-4" />
        </button>
      </div>
      <div className="p-2">
        {items.map((item) => (
          <label
            key={item}
            className="flex items-center space-x-2 py-1 px-2 hover:bg-gray-50 rounded cursor-pointer"
          >
            <input
              type="checkbox"
              checked={selectedItems.includes(item)}
              onChange={() => handleCheckboxChange(item)}
              className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            />
            <span className="text-sm text-gray-700">{item}</span>
          </label>
        ))}
      </div>
    </div>
  );
};
