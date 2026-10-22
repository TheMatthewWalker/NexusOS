export type Rect = { x: number; y: number; width: number; height: number };
export type Tile =
  | "left"
  | "right"
  | "top-left"
  | "top-right"
  | "bottom-left"
  | "bottom-right"
  | "full"
  | "third-left"
  | "third-center"
  | "third-right";
export const TOP = 58,
  BOTTOM = 96,
  GAP = 12;
export function workspace(width: number, height: number): Rect {
  return {
    x: 14,
    y: TOP,
    width: Math.max(240, width - 28),
    height: Math.max(240, height - TOP - BOTTOM),
  };
}
export function tileRect(
  tile: Tile,
  width: number,
  height: number,
  split = 0.5
): Rect {
  const a = workspace(width, height);
  const leftWidth = (a.width - GAP) * split,
    rightWidth = a.width - GAP - leftWidth;
  const rightX = a.x + leftWidth + GAP;
  const halfHeight = (a.height - GAP) / 2;
  if (tile === "full") return a;
  if (tile.startsWith("third-")) {
    const w = (a.width - GAP * 2) / 3;
    const i = tile === "third-left" ? 0 : tile === "third-center" ? 1 : 2;
    return { ...a, x: a.x + i * (w + GAP), width: w };
  }
  const right = tile.includes("right");
  return {
    x: right ? rightX : a.x,
    y: tile.startsWith("bottom") ? a.y + halfHeight + GAP : a.y,
    width: right ? rightWidth : leftWidth,
    height: tile === "left" || tile === "right" ? a.height : halfHeight,
  };
}
export function clampRect(rect: Rect, width: number, height: number): Rect {
  const a = workspace(width, height);
  const w = Math.min(Math.max(rect.width, Math.min(390, a.width)), a.width),
    h = Math.min(Math.max(rect.height, Math.min(300, a.height)), a.height);
  return {
    x: Math.max(a.x, Math.min(rect.x, a.x + a.width - w)),
    y: Math.max(a.y, Math.min(rect.y, a.y + a.height - h)),
    width: w,
    height: h,
  };
}
export function magneticTarget(
  x: number,
  y: number,
  width: number,
  height: number
): Tile | null {
  const edge = 30,
    corner = height * 0.23;
  if (x < edge)
    return y < corner
      ? "top-left"
      : y > height - corner
        ? "bottom-left"
        : "left";
  if (x > width - edge)
    return y < corner
      ? "top-right"
      : y > height - corner
        ? "bottom-right"
        : "right";
  if (y < 72) return "full";
  return null;
}
