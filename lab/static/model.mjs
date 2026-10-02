// Coordinate math is shared by rendering and the offline regression checks.
export function worldToImage(x, z, size, image) {
  return [(x + size[0] / 2) / size[0] * image[0], (size[1] / 2 - z) / size[1] * image[1]];
}
export function screenPoint(x, z, view, width, height) {
  return [width / 2 + (x - view.x) * view.scale, height / 2 - (z - view.z) * view.scale];
}
export function worldPoint(x, y, view, width, height) {
  return [view.x + (x - width / 2) / view.scale, view.z - (y - height / 2) / view.scale];
}
export function latestAt(events, time) {
  let result = null;
  for (const event of events) { if (event.t > time) break; result = event; }
  return result;
}
export function fitPoints(points, width, height, padding = 45) {
  if (!points.length) return null;
  const xs = points.map(p => p[0]), zs = points.map(p => p[1]);
  const minX = Math.min(...xs), maxX = Math.max(...xs), minZ = Math.min(...zs), maxZ = Math.max(...zs);
  return {x: (minX + maxX) / 2, z: (minZ + maxZ) / 2,
    scale: Math.min((width - padding * 2) / Math.max(12, maxX - minX), (height - padding * 2) / Math.max(12, maxZ - minZ))};
}
