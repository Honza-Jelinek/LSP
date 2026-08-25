// Typy zrcadlí DTO ze serveru (LSP.Server/Api).

export type MovieSource = {
  mediaFileId: number
  fileName: string
}

export type Movie = {
  id: number
  mediaFileId: number
  title: string
  year: number | null
  fileName: string
  posterUrl: string | null
  genres: string | null
  sources: MovieSource[]
}

export type Episode = {
  id: number
  mediaFileId: number
  season: number
  number: number
  title: string | null
  fileName: string
}

export type Season = {
  season: number
  episodes: Episode[]
}

export type Show = {
  id: number
  title: string
  posterUrl: string | null
  genres: string | null
  seasons: Season[]
}

export type Library = {
  movies: Movie[]
  shows: Show[]
}

export type AudioTrack = {
  ordinal: number
  streamIndex: number
  codec: string | null
  language: string | null
  normalizedLanguage: string | null
  label: string
  isDefault: boolean
}

export type SubtitleTrack = {
  id: string
  source: 'internal' | 'sidecar'
  ordinal: number | null
  streamIndex: number | null
  codec: string | null
  language: string | null
  normalizedLanguage: string | null
  label: string
  isDefault: boolean
  isForced: boolean
  isPlayable: boolean
  url: string | null
}

export type StreamInfo = {
  mediaFileId: number
  mode: 'direct' | 'hls'
  url: string
  durationSeconds: number | null
  videoCodec: string | null
  audioCodec: string | null
  audioTracks: AudioTrack[]
  selectedAudioOrdinal: number | null
  selectedAudioLanguage: string | null
  subtitleTracks: SubtitleTrack[]
}

export type ScanSummary = {
  filesScanned: number
  movies: number
  shows: number
  episodes: number
  elapsedMs: number
}

export type Progress = {
  positionSeconds: number
  durationSeconds: number | null
  finished: boolean
}

export type LibraryFolder = {
  id: number
  path: string
}

export type ContinueItem = {
  mediaFileId: number
  kind: 'movie' | 'episode'
  showId: number | null
  title: string
  subtitle: string | null
  posterUrl: string | null
  positionSeconds: number
  durationSeconds: number | null
  percent: number
}

export type TmdbCandidate = {
  tmdbId: number
  title: string
  year: number | null
  posterUrl: string | null
  overview: string | null
}

export type NextEpisode = {
  mediaFileId: number
  showTitle: string
  season: number
  number: number
  title: string | null
}

export type ReviewItem = {
  id: number
  mediaFileId: number
  kind: 'movie' | 'show'
  title: string
  year: number | null
  fileName: string
  tmdbId: number | null
  mediaType: string | null
  filePath: string | null
  season: number | null
  episode: number | null
  showId: number | null
  showTitle: string | null
}

export type ReviewQueue = {
  movies: ReviewItem[]
  shows: ReviewItem[]
}

export type HomeItem = {
  kind: 'movie' | 'show' | 'episode'
  mediaFileId: number | null
  showId: number | null
  title: string
  subtitle: string | null
  posterUrl: string | null
  badge: string | null
  progress: number | null
}

export type HomeRow = {
  key: string
  title: string
  items: HomeItem[]
}

export type EnrichProgress = {
  phase: number
  phaseName: string
  processed: number
  total: number
}

export type EnrichSummary = {
  tmdbHits: number
  tmdbMisses: number
  llmFallbacks: number
  llmRecovered: number
  reclassified: number
  postersDownloaded: number
  skipped: number
  elapsedMs: number
  reviewQueue: number
  error: string | null
}

export type EnrichStatus = {
  state: 'idle' | 'running' | 'completed' | 'failed' | 'cancelled'
  started: boolean | null
  force: boolean
  startedAt: string | null
  finishedAt: string | null
  progress: EnrichProgress | null
  summary: EnrichSummary | null
}

export type ExportProgress = {
  phase: string
  filesDone: number
  filesTotal: number
  bytesDone: number
  bytesTotal: number
  currentFile: string | null
}

export type ExportReport = {
  extended: boolean
  newFiles: number
  existingFiles: number
  skippedFiles: number
  unmatchedFiles: string[]
  failures: string[]
}

export type ExportStatus = {
  state: 'idle' | 'running' | 'completed' | 'failed' | 'cancelled'
  started: boolean | null
  targetRoot: string | null
  move: boolean
  includePosters: boolean
  extended: boolean
  startedAt: string | null
  finishedAt: string | null
  progress: ExportProgress | null
  report: ExportReport | null
  error: string | null
}

export type ImportResult = {
  packageRoot: string
  postersCopied: number
}

export type FetchPreferences = {
  posters: boolean
  backdrops: boolean
  overview: boolean
  rating: boolean
  genres: boolean
  cast: boolean
}

export type AppConfig = {
  hasTmdbKey: boolean
  llmProvider: string
  llmModel: string
  hasLlmKey: boolean
  planMode: string
  enrichAuto: boolean
  fetch: FetchPreferences
}

export type SaveConfigRequest = {
  tmdbApiKey?: string
  llmProvider?: string
  llmModel?: string
  llmApiKey?: string
  planMode?: string
  enrichAuto?: boolean
  fetch?: FetchPreferences
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res.json() as Promise<T>
}

async function ensureOk(res: Response): Promise<Response> {
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res
}

export type SeasonEpisode = {
  number: number
  title: string | null
}

export type TvEpisode = {
  season: number
  number: number
  title: string | null
}

export const api = {
  getLibrary: () => fetch('/api/library').then(json<Library>),
  scan: () => fetch('/api/library/scan', { method: 'POST' }).then(json<ScanSummary>),
  getStreamInfo: (mediaFileId: number, audioOrdinal?: number | null) => {
    const qs = audioOrdinal == null ? '' : `?audio=${audioOrdinal}`
    return fetch(`/api/stream/${mediaFileId}/info${qs}`).then(json<StreamInfo>)
  },

  saveProgress: (mediaFileId: number, positionSeconds: number, durationSeconds: number | null) =>
    fetch('/api/progress', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mediaFileId, positionSeconds, durationSeconds }),
    }),

  getProgress: async (mediaFileId: number): Promise<Progress | null> => {
    const res = await fetch(`/api/progress/${mediaFileId}`)
    if (res.status === 204 || !res.ok) return null
    return res.json() as Promise<Progress>
  },

  getContinueWatching: () => fetch('/api/continue-watching').then(json<ContinueItem[]>),
  deleteProgress: (mediaFileId: number) =>
    fetch(`/api/progress/${mediaFileId}`, { method: 'DELETE' }).then(ensureOk),
  deleteShowProgress: (showId: number) =>
    fetch(`/api/progress/show/${showId}`, { method: 'DELETE' }).then(ensureOk),
  /** Zahodí transkód segmenty souboru (voláno při přepnutí na jinou epizodu). */
  purgeSegments: (mediaFileId: number) =>
    fetch(`/api/stream/${mediaFileId}/segments`, { method: 'DELETE' }).then(ensureOk),

  getLibraryFolders: () => fetch('/api/settings/libraries').then(json<LibraryFolder[]>),
  addLibraryFolder: (path: string) =>
    fetch('/api/settings/libraries', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path }),
    }),
  removeLibraryFolder: (id: number) =>
    fetch(`/api/settings/libraries/${id}`, { method: 'DELETE' }),
  scanAll: () => fetch('/api/settings/libraries/scan', { method: 'POST' }).then(json<ScanSummary>),
  clearLibrary: () => fetch('/api/settings/clear-library', { method: 'POST' }),
  browseFolder: async (): Promise<string | null> => {
    const res = await fetch('/api/settings/libraries/browse', { method: 'POST' })
    if (res.status === 204) return null
    if (!res.ok) return null
    const data = await res.json() as { path: string }
    return data.path
  },

  startEnrich: (force = false) =>
    fetch(`/api/enrich?force=${force}`, { method: 'POST' }).then(json<EnrichStatus>),
  getEnrichStatus: () => fetch('/api/enrich/status').then(json<EnrichStatus>),
  cancelEnrich: () => fetch('/api/enrich/cancel', { method: 'POST' }).then(json<EnrichStatus>),
  startExport: (targetRoot: string, mediaFileIds: number[], move: boolean, includePosters: boolean) =>
    fetch('/api/export', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ targetRoot, mediaFileIds, move, includePosters }),
    }).then(json<ExportStatus>),
  getExportStatus: () => fetch('/api/export/status').then(json<ExportStatus>),
  cancelExport: () => fetch('/api/export/cancel', { method: 'POST' }).then(json<ExportStatus>),
  importPackage: (packageRoot: string) =>
    fetch('/api/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ packageRoot }),
    }).then(json<ImportResult>),
  ping: () => fetch('/api/ping').then((r) => r.ok),

  getConfig: () => fetch('/api/settings/config').then(json<AppConfig>),
  saveConfig: (req: SaveConfigRequest) =>
    fetch('/api/settings/config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    }),
  savePlayerAudioLanguage: (language: string | null) =>
    fetch('/api/settings/player/audio-language', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ language }),
    }).then(ensureOk),

  searchTmdb: (q: string, type: 'movie' | 'tv') =>
    fetch(`/api/tmdb/search?q=${encodeURIComponent(q)}&type=${type}`).then(json<TmdbCandidate[]>),
  getTvSeasonEpisodes: (tmdbId: number, season: number) =>
    fetch(`/api/tmdb/tv/${tmdbId}/season/${season}/episodes`).then(json<SeasonEpisode[]>),
  getTvAllEpisodes: (tmdbId: number) =>
    fetch(`/api/tmdb/tv/${tmdbId}/episodes`).then(json<TvEpisode[]>),
  manualMatchFile: (req: { mediaFileId: number; kind: 'movie' | 'episode' | 'lazy-episode'; tmdbId: number; mediaType: 'movie' | 'tv' | 'none'; season?: number; episode?: number; showTitle?: string }) =>
    fetch('/api/library/manual-match', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    }).then(ensureOk),
  lazyShow: (showId: number) =>
    fetch(`/api/library/lazy-show/${showId}`, { method: 'POST' }).then(ensureOk),
  manualMatchShow: (showId: number, tmdbId: number) =>
    fetch('/api/library/manual-match-show', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ showId, tmdbId }),
    }).then(ensureOk),
  resetMatchFile: (mediaFileId: number) =>
    fetch(`/api/library/manual-match/reset-file/${mediaFileId}`, { method: 'POST' }).then(ensureOk),
  resetMatchShow: (showId: number) =>
    fetch(`/api/library/manual-match/reset-show/${showId}`, { method: 'POST' }).then(ensureOk),
  deleteRecordFile: (mediaFileId: number) =>
    fetch(`/api/library/delete-record/file/${mediaFileId}`, { method: 'POST' }).then(ensureOk),
  deleteRecordShow: (showId: number) =>
    fetch(`/api/library/delete-record/show/${showId}`, { method: 'POST' }).then(ensureOk),

  getNextEpisode: async (mediaFileId: number): Promise<NextEpisode | null> => {
    const res = await fetch(`/api/next-episode/${mediaFileId}`)
    if (res.status === 204 || !res.ok) return null
    return res.json() as Promise<NextEpisode>
  },

  getPreviousEpisode: async (mediaFileId: number): Promise<NextEpisode | null> => {
    const res = await fetch(`/api/previous-episode/${mediaFileId}`)
    if (res.status === 204 || !res.ok) return null
    return res.json() as Promise<NextEpisode>
  },

  getReviewQueue: () => fetch('/api/review-queue').then(json<ReviewQueue>),

  getHome: () => fetch('/api/home').then(json<HomeRow[]>),
}
