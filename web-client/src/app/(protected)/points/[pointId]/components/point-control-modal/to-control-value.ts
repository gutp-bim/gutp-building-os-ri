/**
 * Converts a UI control value to the numeric form the point-control API expects.
 * The backend's ControlValueValidator treats a boolean-typed point as 0/1
 * (BuildingOS.Shared/Domain/ControlValueValidator.cs), so `true`/`false` map to 1/0.
 */
export function toControlValue(value: number | boolean): number {
  return typeof value === "boolean" ? (value ? 1 : 0) : value;
}
