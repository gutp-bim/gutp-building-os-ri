export const unitLabelMap: Record<string, string> = {
  "http://qudt.org/vocab/unit/KiloW-HR": "kWh",
  "http://qudt.org/vocab/unit/DEG_C": "°C",
  "http://qudt.org/vocab/unit/PERCENT": "%",
  "http://qudt.org/vocab/unit/M": "m",
  "http://qudt.org/vocab/unit/KiloGM": "kg",
  "http://qudt.org/vocab/unit/SEC": "s",
  "http://qudt.org/vocab/unit/M-PER-SEC": "m/s",
  "http://qudt.org/vocab/unit/DEG": "°",
  "http://qudt.org/vocab/unit/Hz": "Hz",
  "http://qudt.org/vocab/unit/K": "K",
  "http://qudt.org/vocab/unit/L": "L",
  "http://qudt.org/vocab/unit/Milli-L": "mL",
  "http://qudt.org/vocab/unit/Milli-M": "mm",
  "http://qudt.org/vocab/unit/Centi-M": "cm",
  "http://qudt.org/vocab/unit/Kilo-M": "km",
  "http://qudt.org/vocab/unit/Milli-SEC": "ms",
  "http://qudt.org/vocab/unit/Mega-W": "MW",
  "http://qudt.org/vocab/unit/W": "W",
  "http://qudt.org/vocab/unit/Volt": "V",
  "http://qudt.org/vocab/unit/Ampere": "A",
  "http://qudt.org/vocab/unit/Ohm": "Ω",
  "http://qudt.org/vocab/unit/Bar": "bar",
  "http://qudt.org/vocab/unit/Pa": "Pa",
  "http://qudt.org/vocab/unit/LUX": "lx",
  "http://qudt.org/vocab/unit/DEG_F": "°F",
  "http://qudt.org/vocab/unit/DEG_K": "K",
  "http://qudt.org/vocab/unit/TONNE": "t",
  "http://qudt.org/vocab/unit/GM": "g",
  "http://qudt.org/vocab/unit/Milli-GM": "mg",
  "http://qudt.org/vocab/unit/DEG_R": "°R",
  "http://qudt.org/vocab/unit/DEG_N": "°N",
  "http://qudt.org/vocab/unit/DEG_RE": "°Ré",
  "http://qudt.org/vocab/unit/DEG_D": "°D",
  "http://qudt.org/vocab/unit/DEG_Ra": "°Ra",
  "http://qudt.org/vocab/unit/DEG_Ro": "°Rø",
  "http://qudt.org/vocab/unit/DEG_De": "°De",
  "http://qudt.org/vocab/unit/DEG_Re": "°Ré",
};

/**
 * QUDT IRI ではなく短縮コード（`degC` など）で twin に入っている単位の別名表。
 * CSV 由来のポイントリストは IRI ではなくこの短縮表記を持ち込むことがあるため、
 * IRI 表を引けなかった場合の 2 段目として引く。
 */
const unitAliasMap: Record<string, string> = {
  degC: "°C",
  degF: "°F",
  degK: "K",
  degRe: "°Ré",
  percent: "%",
  pct: "%",
};

/**
 * 表示用の単位ラベルを解決する（QUDT IRI → 短縮コード別名 → 生値の順）。
 * 未設定（null / 空文字）は「単位なし」として null を返すので、呼び出し側は単位を付けない。
 */
export function resolveUnitLabel(
  unit: string | null | undefined,
): string | null {
  if (!unit) return null;
  return unitLabelMap[unit] ?? unitAliasMap[unit] ?? unit;
}
