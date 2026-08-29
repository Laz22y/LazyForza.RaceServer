export * from "./protocol.generated";

export interface RaceEnvelope<T = unknown> {
  protocolVersion: number;
  type: string;
  sequence: number;
  payload: T;
}

export function clamp(value: unknown, minimum: number, maximum: number): number {
  const numeric = typeof value === "number" && Number.isFinite(value) ? value : minimum;
  return Math.min(maximum, Math.max(minimum, numeric));
}

export function clampInteger(value: unknown, minimum: number, maximum: number): number {
  return Math.round(clamp(value, minimum, maximum));
}

export function cleanText(value: unknown, maximumLength: number): string | null {
  if (typeof value !== "string") return null;
  const cleaned = [...value.trim()].filter(character => character >= " " && character !== "\u007f").join("");
  return cleaned.length === 0 ? null : cleaned.slice(0, maximumLength);
}

export function isThemeColor(value: unknown): value is string {
  return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value);
}
