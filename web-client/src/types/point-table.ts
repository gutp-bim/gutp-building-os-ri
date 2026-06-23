export type PointTableField =
  | "name"
  | "dataSpecification"
  | "dataType"
  | "writable"
  | "targetArea";

export type PointTableHeader = {
  field: PointTableField;
  label: string;
};
