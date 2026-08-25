export type PortableExportTarget =
  | { kind: 'movie'; mediaFileId: number }
  | { kind: 'show'; showId: number }

export function portableExportPath(target: PortableExportTarget): string {
  const value = target.kind === 'movie'
    ? `movie:${target.mediaFileId}`
    : `show:${target.showId}`
  const query = new URLSearchParams({ export: value })
  return `/settings?${query.toString()}#portable-export`
}
