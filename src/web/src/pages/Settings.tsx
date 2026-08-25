import { Fragment, useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControlLabel,
  FormControl,
  FormLabel,
  FormGroup,
  IconButton,
  LinearProgress,
  Link,
  List,
  ListItem,
  ListItemIcon,
  ListItemText,
  Radio,
  RadioGroup,
  Switch,
  TextField,
  Typography,
} from '@mui/material'
import FolderIcon from '@mui/icons-material/Folder'
import DeleteIcon from '@mui/icons-material/Delete'
import CreateNewFolderIcon from '@mui/icons-material/CreateNewFolder'
import RefreshIcon from '@mui/icons-material/Refresh'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import { api, type AppConfig, type EnrichStatus, type ExportStatus, type FetchPreferences, type LibraryFolder } from '../api'
import { useLibrary } from '../library'

const defaultFetch: FetchPreferences = {
  posters: true, backdrops: true, overview: true, rating: true, genres: true, cast: true,
}

function tileInitials(title: string): string {
  return title.split(/\s+/).filter(Boolean).slice(0, 2).map((word) => word[0]?.toUpperCase() ?? '').join('')
}

type ExportTileProps = {
  title: string
  subtitle?: string
  posterUrl: string | null
  checked: boolean
  indeterminate?: boolean
  expanded?: boolean
  onBodyClick: () => void
  onToggle: () => void
}

function ExportTile({ title, subtitle, posterUrl, checked, indeterminate, expanded, onBodyClick, onToggle }: ExportTileProps) {
  const hue = [...title].reduce((hash, ch) => (hash * 31 + ch.charCodeAt(0)) % 360, 0)
  const selected = checked || indeterminate
  return (
    <Box
      onClick={onBodyClick}
      sx={{
        position: 'relative',
        cursor: 'pointer',
        borderRadius: 1.5,
        overflow: 'hidden',
        outline: selected || expanded ? '2px solid' : '1px solid',
        outlineColor: selected || expanded ? 'primary.main' : 'divider',
        outlineOffset: -1,
        transition: 'transform 0.15s ease',
        '&:hover': { transform: 'scale(1.03)' },
      }}
    >
      <Box
        sx={{
          aspectRatio: '2/3',
          background: `linear-gradient(150deg, hsl(${hue} 45% 28%), hsl(${(hue + 40) % 360} 50% 14%))`,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
        }}
      >
        {posterUrl ? (
          <Box component="img" src={posterUrl} alt={title} loading="lazy"
            sx={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
        ) : (
          <Typography sx={{ fontSize: '1.6rem', fontWeight: 800, color: 'rgba(255,255,255,0.85)' }}>
            {tileInitials(title)}
          </Typography>
        )}
      </Box>
      <Checkbox
        size="small"
        checked={checked}
        indeterminate={indeterminate}
        onClick={(event) => { event.stopPropagation(); onToggle() }}
        sx={{
          position: 'absolute',
          top: 4,
          right: 4,
          p: 0.5,
          color: 'rgba(255,255,255,0.85)',
          bgcolor: 'rgba(0,0,0,0.55)',
          borderRadius: '50%',
          '&:hover': { bgcolor: 'rgba(0,0,0,0.75)' },
          '&.Mui-checked, &.MuiCheckbox-indeterminate': { color: 'primary.light', bgcolor: 'rgba(0,0,0,0.7)' },
        }}
      />
      <Box
        sx={{
          position: 'absolute',
          left: 0, right: 0, bottom: 0,
          px: 1, pt: 2.5, pb: 0.75,
          background: 'linear-gradient(transparent, rgba(0,0,0,0.85))',
        }}
      >
        <Typography variant="caption" noWrap sx={{ display: 'block', color: '#fff', fontWeight: 600 }}>
          {title}
        </Typography>
        {subtitle && (
          <Typography variant="caption" noWrap sx={{ display: 'block', color: 'rgba(255,255,255,0.7)' }}>
            {subtitle}
          </Typography>
        )}
      </Box>
    </Box>
  )
}

export function Settings() {
  const { library, scanning, reload } = useLibrary()
  const [searchParams] = useSearchParams()
  const exportRequest = searchParams.get('export')
  const [folders, setFolders] = useState<LibraryFolder[]>([])
  const [error, setError] = useState<string | null>(null)
  const [scanResult, setScanResult] = useState<string | null>(null)
  const [isBusy, setIsBusy] = useState(false)

  // Config
  const [config, setConfig] = useState<AppConfig | null>(null)
  const [tmdbKey, setTmdbKey] = useState('')
  const [llmKey, setLlmKey] = useState('')
  const [llmProvider, setLlmProvider] = useState('anthropic')
  const [llmModel, setLlmModel] = useState('claude-haiku-4-5-20251001')
  const [enrichAuto, setEnrichAuto] = useState(false)
  const [fetchPrefs, setFetchPrefs] = useState<FetchPreferences>(defaultFetch)
  const [saved, setSaved] = useState(false)
  const [enrichStatus, setEnrichStatus] = useState<EnrichStatus | null>(null)
  const [enrichResult, setEnrichResult] = useState<string | null>(null)
  const enriching = enrichStatus?.state === 'running'
  const prevEnrichState = useRef<EnrichStatus['state'] | undefined>(undefined)
  const reloadRef = useRef(reload)
  reloadRef.current = reload

  // Prenosny disk
  const [exportTarget, setExportTarget] = useState('')
  const [exportMode, setExportMode] = useState<'copy' | 'move'>('copy')
  const [includePosters, setIncludePosters] = useState(true)
  const [selectionOpen, setSelectionOpen] = useState(false)
  const [expandedShow, setExpandedShow] = useState<number | null>(null)
  const [mediaSearch, setMediaSearch] = useState('')
  const [selectedExportItems, setSelectedExportItems] = useState<Set<string>>(() => new Set())
  const [exportStatus, setExportStatus] = useState<ExportStatus | null>(null)
  const [importResult, setImportResult] = useState<string | null>(null)
  const exporting = exportStatus?.state === 'running'
  const prevExportState = useRef<ExportStatus['state'] | undefined>(undefined)
  const handledExportRequest = useRef<string | null>(null)

  const movieCount = library?.movies.length ?? 0
  const showCount = library?.shows.length ?? 0
  const episodeCount =
    library?.shows.reduce((n, s) => n + s.seasons.reduce((m, ss) => m + ss.episodes.length, 0), 0) ?? 0

  const exportMovies = library?.movies.map((movie) => ({
    key: `movie:${movie.id}`,
    title: movie.year ? `${movie.title} (${movie.year})` : movie.title,
    posterUrl: movie.posterUrl,
    mediaFileIds: movie.sources.length > 0
      ? movie.sources.map((source) => source.mediaFileId)
      : [movie.mediaFileId],
  })) ?? []
  const exportShows = library?.shows.map((show) => ({
    showId: show.id,
    title: show.title,
    posterUrl: show.posterUrl,
    seasons: show.seasons
      .map((season) => ({
        key: `season:${show.id}:${season.season}`,
        season: season.season,
        mediaFileIds: season.episodes.map((episode) => episode.mediaFileId),
      }))
      .filter((season) => season.mediaFileIds.length > 0),
  })).filter((show) => show.seasons.length > 0) ?? []

  const normalizedSearch = mediaSearch.trim().toLocaleLowerCase('cs')
  const matchesSearch = (title: string) =>
    !normalizedSearch || title.toLocaleLowerCase('cs').includes(normalizedSearch)
  const filteredMovies = exportMovies.filter((movie) => matchesSearch(movie.title))
  const filteredShows = exportShows.filter((show) => matchesSearch(show.title))

  const selectedMovieCount = exportMovies.filter((movie) => selectedExportItems.has(movie.key)).length
  const selectedSeasonCount = exportShows.reduce(
    (count, show) => count + show.seasons.filter((season) => selectedExportItems.has(season.key)).length, 0)
  const selectedMediaFileIds = [...new Set([
    ...exportMovies.filter((movie) => selectedExportItems.has(movie.key)).flatMap((movie) => movie.mediaFileIds),
    ...exportShows.flatMap((show) =>
      show.seasons.filter((season) => selectedExportItems.has(season.key)).flatMap((season) => season.mediaFileIds)),
  ])]

  const loadFolders = () => api.getLibraryFolders().then(setFolders).catch(() => {})
  const loadConfig = () =>
    api.getConfig().then((c) => {
      setConfig(c)
      setLlmProvider(c.llmProvider)
      setLlmModel(c.llmModel)
      setEnrichAuto(c.enrichAuto)
      setFetchPrefs(c.fetch)
    }).catch(() => {})

  useEffect(() => {
    void loadFolders()
    void loadConfig()
  }, [])

  useEffect(() => {
    if (!library || !exportRequest || handledExportRequest.current === exportRequest) return
    handledExportRequest.current = exportRequest

    const [kind, rawId] = exportRequest.split(':', 2)
    const id = Number(rawId)
    if (!Number.isInteger(id)) {
      setError('Požadovanou položku pro export se nepodařilo rozpoznat.')
      return
    }

    if (kind === 'movie') {
      const movie = library.movies.find((item) =>
        item.mediaFileId === id || item.sources.some((source) => source.mediaFileId === id))
      if (!movie) {
        setError('Požadovaný film už není v knihovně.')
        return
      }
      setSelectedExportItems(new Set([`movie:${movie.id}`]))
    } else if (kind === 'show') {
      const show = library.shows.find((item) => item.id === id)
      if (!show) {
        setError('Požadovaný seriál už není v knihovně.')
        return
      }
      setMediaSearch(show.title)
      setExpandedShow(show.id)
      setSelectionOpen(true)
    } else {
      setError('Požadovanou položku pro export se nepodařilo rozpoznat.')
      return
    }

    window.requestAnimationFrame(() => {
      document.getElementById('portable-export')?.scrollIntoView({ block: 'start' })
    })
  }, [exportRequest, library])

  useEffect(() => {
    const tick = () => { api.getExportStatus().then(setExportStatus).catch(() => {}) }
    tick()
    const timer = window.setInterval(tick, 2000)
    return () => window.clearInterval(timer)
  }, [])

  useEffect(() => {
    if (!exportStatus) return
    const prev = prevExportState.current
    if (prev === 'running' && exportStatus.state === 'completed') {
      void reloadRef.current()
    }
    if (prev === 'running' && exportStatus.state === 'failed') {
      setError(exportStatus.error ?? 'Export selhal.')
    }
    prevExportState.current = exportStatus.state
  }, [exportStatus])

  // Poluje stav enrichmentu — na mount hned (aby se okno znovuotevřené k běžícímu
  // serveru na pozadí okamžitě "chytilo" rozdělaného jobu), pak každé 2 s.
  useEffect(() => {
    const tick = () => { api.getEnrichStatus().then(setEnrichStatus).catch(() => {}) }
    tick()
    const timer = window.setInterval(tick, 2000)
    return () => window.clearInterval(timer)
  }, [])

  // Reaguje na dokončení/selhání jobu (i takového, který spustila jiná instance okna).
  useEffect(() => {
    if (!enrichStatus) return
    const prev = prevEnrichState.current
    if (prev === 'running' && enrichStatus.state === 'completed' && enrichStatus.summary) {
      const r = enrichStatus.summary
      const parts = [`TMDB: ${r.tmdbHits} nalezeno, ${r.tmdbMisses} nenalezeno, ${r.postersDownloaded} posterů`]
      if (r.llmFallbacks > 0)
        parts.push(`LLM: ${r.llmFallbacks} → ${r.llmRecovered} zotaveno (${r.reclassified} překlasifikováno)`)
      if (r.reviewQueue > 0) parts.push(`${r.reviewQueue} k ruční kontrole`)
      parts.push(`(${(r.elapsedMs / 1000).toFixed(1)} s)`)
      setEnrichResult(parts.join(' · '))
      void reloadRef.current()
    }
    if (prev === 'running' && enrichStatus.state === 'failed') {
      setError(enrichStatus.summary?.error ?? 'Obohacení selhalo.')
    }
    prevEnrichState.current = enrichStatus.state
  }, [enrichStatus])

  const browseAndAdd = async () => {
    setError(null)
    const path = await api.browseFolder()
    if (!path) return
    const res = await api.addLibraryFolder(path)
    if (!res.ok) {
      const text = await res.text()
      setError(text || `Chyba ${res.status}`)
      return
    }
    await loadFolders()
  }

  const removeFolder = async (id: number) => {
    await api.removeLibraryFolder(id)
    await loadFolders()
  }

  const scanAll = async () => {
    setIsBusy(true)
    setScanResult(null)
    try {
      const summary = await api.scanAll()
      setScanResult(`Nalezeno: ${summary.filesScanned} souborů, ${summary.movies} filmů, ${summary.shows} seriálů, ${summary.episodes} epizod (${summary.elapsedMs} ms)`)
      await reload()
    } catch {
      setError('Skenování selhalo.')
    } finally {
      setIsBusy(false)
    }
  }

  const saveApiKeys = async () => {
    setSaved(false)
    await api.saveConfig({
      ...(tmdbKey ? { tmdbApiKey: tmdbKey } : {}),
      ...(llmKey ? { llmApiKey: llmKey } : {}),
      llmProvider,
      llmModel,
      enrichAuto,
      fetch: fetchPrefs,
    })
    setTmdbKey('')
    setLlmKey('')
    await loadConfig()
    setSaved(true)
    setTimeout(() => setSaved(false), 3000)
  }

  const fp = (key: keyof FetchPreferences, val: boolean) =>
    setFetchPrefs((prev) => ({ ...prev, [key]: val }))

  const runEnrich = async (force: boolean) => {
    setError(null)
    setEnrichResult(null)
    try {
      // Pokud už job běží (třeba spuštěný jinou instancí okna), server ho vrátí jako started:false
      // a my ho jen "adoptujeme" — žádná chyba, žádný druhý běh.
      setEnrichStatus(await api.startEnrich(force))
    } catch {
      setError('Obohacení se nepodařilo spustit.')
    }
  }

  const cancelEnrich = async () => {
    try { setEnrichStatus(await api.cancelEnrich()) } catch { /* ignore */ }
  }

  const browseExportTarget = async () => {
    const path = await api.browseFolder()
    if (path) setExportTarget(path)
  }

  const startExport = async () => {
    if (!exportTarget || selectedMediaFileIds.length === 0) return
    setError(null)
    setImportResult(null)
    try {
      setExportStatus(await api.startExport(
        exportTarget,
        selectedMediaFileIds,
        exportMode === 'move',
        includePosters))
    } catch {
      setError('Export se nepodařilo spustit. Zkontroluj, zda neběží sken nebo obohacení.')
    }
  }

  const cancelExport = async () => {
    try { setExportStatus(await api.cancelExport()) } catch { /* ignore */ }
  }

  const toggleExportItem = (key: string) => {
    setSelectedExportItems((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const setExportItems = (keys: string[], on: boolean) => {
    setSelectedExportItems((current) => {
      const next = new Set(current)
      for (const key of keys) {
        if (on) next.add(key)
        else next.delete(key)
      }
      return next
    })
  }

  const selectVisibleExportItems = () => {
    setExportItems([
      ...filteredMovies.map((movie) => movie.key),
      ...filteredShows.flatMap((show) => show.seasons.map((season) => season.key)),
    ], true)
  }

  const importPackage = async () => {
    const path = await api.browseFolder()
    if (!path) return
    if (!window.confirm('Import nahradí aktuální knihovnu. Videa zůstanou na vybraném externím disku. Pokračovat?')) return

    setError(null)
    try {
      const result = await api.importPackage(path)
      setImportResult(`Knihovna importována z ${result.packageRoot} · ${result.postersCopied} plakátů`)
      await reload()
    } catch {
      setError('Import selhal. Ověř, že vybraná složka obsahuje data\\library.db a neběží jiná operace.')
    }
  }

  return (
    <Box sx={{ p: 3, maxWidth: 700 }}>
      <Typography variant="h5" sx={{ mb: 3 }}>Nastavení</Typography>

      {/* --- Knihovní složky --- */}
      <Typography variant="subtitle1" sx={{ fontWeight: 600, mb: 1 }}>Knihovní složky</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
        Přidej složky, kde máš uložené filmy a seriály. Po přidání/odebrání klikni Přeskenovat.
      </Typography>

      <List dense>
        {folders.map((f) => (
          <ListItem
            key={f.id}
            secondaryAction={
              <IconButton edge="end" onClick={() => removeFolder(f.id)} title="Odebrat">
                <DeleteIcon />
              </IconButton>
            }
          >
            <ListItemIcon><FolderIcon /></ListItemIcon>
            <ListItemText primary={f.path} />
          </ListItem>
        ))}
        {folders.length === 0 && (
          <Typography variant="body2" color="text.secondary" sx={{ py: 1, px: 2 }}>
            Zatím žádné složky. Přidej cestu ke svým médiím.
          </Typography>
        )}
      </List>

      <Box sx={{ mt: 1, mb: 2 }}>
        <Button variant="contained" startIcon={<CreateNewFolderIcon />} onClick={browseAndAdd}>
          Přidat složku
        </Button>
      </Box>

      {error && <Alert severity="error" sx={{ mb: 2 }} onClose={() => setError(null)}>{error}</Alert>}

      <Box sx={{ display: 'flex', gap: 1 }}>
        <Button variant="outlined" startIcon={<RefreshIcon />} onClick={scanAll} disabled={isBusy || scanning || folders.length === 0}>
          {isBusy ? 'Skenuji…' : 'Přeskenovat knihovnu'}
        </Button>
        <Button
          variant="outlined" color="error" startIcon={<DeleteIcon />} disabled={isBusy}
          onClick={async () => {
            if (!window.confirm('Vymazat celou knihovnu a cache? Klíče a složky zůstanou. Poté přeskenuj.')) return
            setIsBusy(true)
            try { await api.clearLibrary(); await reload(); setScanResult('Knihovna vymazána. Teď klikni Přeskenovat.') }
            finally { setIsBusy(false) }
          }}
        >
          Vymazat databázi
        </Button>
      </Box>

      {scanResult && <Alert severity="success" sx={{ mt: 2 }}>{scanResult}</Alert>}

      <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
        {movieCount} filmů · {showCount} seriálů · {episodeCount} epizod
      </Typography>

      <Divider sx={{ my: 3 }} />

      {/* --- Plán --- */}
      <Typography variant="subtitle1" sx={{ fontWeight: 600, mb: 1 }}>Plán</Typography>
      <Box sx={{ display: 'flex', gap: 1, mb: 2 }}>
        <Chip label="Free" color="primary" variant="filled" />
        <Chip label="Subscription (brzy)" variant="outlined" disabled />
      </Box>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
        Free režim — spravuj si vlastní API klíče. Subscription s managed klíči bude k dispozici později.
      </Typography>

      <Divider sx={{ my: 3 }} />

      {/* --- API klíče --- */}
      <Typography variant="subtitle1" sx={{ fontWeight: 600, mb: 1 }}>API klíče</Typography>

      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, mb: 2 }}>
        <Box>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
            <Typography variant="body2">TMDB API klíč</Typography>
            {config?.hasTmdbKey && <CheckCircleIcon color="success" sx={{ fontSize: 18 }} />}
          </Box>
          <TextField
            size="small"
            fullWidth
            placeholder={config?.hasTmdbKey ? 'Nastaveno ••••  (zadej nový pro změnu)' : 'Vlož TMDB API klíč'}
            value={tmdbKey}
            onChange={(e) => setTmdbKey(e.target.value)}
          />
          <Typography variant="caption" color="text.secondary">
            Získej zdarma na{' '}
            <Link href="https://www.themoviedb.org/settings/api" target="_blank" rel="noopener">
              themoviedb.org/settings/api
            </Link>
          </Typography>
        </Box>

        <Box>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
            <Typography variant="body2">LLM API klíč (parser — OpenRouter)</Typography>
            {config?.hasLlmKey && <CheckCircleIcon color="success" sx={{ fontSize: 18 }} />}
          </Box>
          <Box sx={{ display: 'flex', gap: 1, mb: 0.5 }}>
            <TextField
              size="small"
              sx={{ flex: 1 }}
              placeholder={config?.hasLlmKey ? 'Nastaveno •••• (zadej nový pro změnu)' : 'Vlož OpenRouter API klíč'}
              value={llmKey}
              onChange={(e) => setLlmKey(e.target.value)}
            />
          </Box>
          <TextField
            size="small"
            fullWidth
            label="Model"
            value={llmModel}
            onChange={(e) => setLlmModel(e.target.value)}
            helperText="Např. anthropic/claude-haiku-4-5, openai/gpt-4o-mini, google/gemini-flash-1.5"
            sx={{ mb: 0.5 }}
          />
          <Typography variant="caption" color="text.secondary">
            LLM parser se volá jen pro soubory, které TMDB nerozpozná (fallback). Získej klíč na{' '}
            <Link href="https://openrouter.ai/keys" target="_blank" rel="noopener">
              openrouter.ai/keys
            </Link>
          </Typography>
        </Box>
      </Box>

      <Divider sx={{ my: 3 }} />

      {/* --- TMDB fetch preferences --- */}
      <Typography variant="subtitle1" sx={{ fontWeight: 600, mb: 0.5 }}>Co stahovat z TMDB</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
        Vypni data, která nepotřebuješ — ušetříš místo na disku a requesty.
      </Typography>
      <FormGroup sx={{ pl: 1 }}>
        <FormControlLabel control={<Checkbox checked={fetchPrefs.posters} onChange={(_, v) => fp('posters', v)} />} label="Postery" />
        <FormControlLabel control={<Checkbox checked={fetchPrefs.backdrops} onChange={(_, v) => fp('backdrops', v)} />} label="Backdropy (pozadí)" />
        <FormControlLabel control={<Checkbox checked={fetchPrefs.overview} onChange={(_, v) => fp('overview', v)} />} label="Přehled (popis děje)" />
        <FormControlLabel control={<Checkbox checked={fetchPrefs.rating} onChange={(_, v) => fp('rating', v)} />} label="Hodnocení" />
        <FormControlLabel control={<Checkbox checked={fetchPrefs.genres} onChange={(_, v) => fp('genres', v)} />} label="Žánry" />
        <FormControlLabel control={<Checkbox checked={fetchPrefs.cast} onChange={(_, v) => fp('cast', v)} />} label="Obsazení" />
      </FormGroup>

      <Divider sx={{ my: 3 }} />

      {/* --- Obohacení --- */}
      <Typography variant="subtitle1" sx={{ fontWeight: 600, mb: 1 }}>Obohacení knihovny</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
        Stáhne postery a metadata z TMDB. LLM parser se volá jen pro soubory, které TMDB nerozpozná.
      </Typography>
      <FormControlLabel
        control={<Switch checked={enrichAuto} onChange={(_, v) => setEnrichAuto(v)} />}
        label="Obohacovat automaticky po skenu"
      />
      <Box sx={{ display: 'flex', gap: 1, mt: 1 }}>
        <Button variant="contained" disabled={enriching || !config?.hasTmdbKey} onClick={() => runEnrich(false)}>
          {enriching ? 'Obohacuji…' : 'Obohatit knihovnu'}
        </Button>
        <Button variant="outlined" disabled={enriching || !config?.hasTmdbKey} onClick={() => runEnrich(true)}>
          Přegenerovat vše
        </Button>
        {enriching && (
          <Button variant="text" color="error" onClick={cancelEnrich}>
            Zrušit
          </Button>
        )}
      </Box>
      {enriching && enrichStatus?.progress && (
        <Box sx={{ mt: 1.5, maxWidth: 400 }}>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 0.5 }}>
            Fáze {enrichStatus.progress.phase} — {enrichStatus.progress.phaseName}
            {enrichStatus.progress.total > 0 && ` (${enrichStatus.progress.processed}/${enrichStatus.progress.total})`}
          </Typography>
          <LinearProgress
            variant={enrichStatus.progress.total > 0 ? 'determinate' : 'indeterminate'}
            value={enrichStatus.progress.total > 0
              ? (enrichStatus.progress.processed / enrichStatus.progress.total) * 100
              : undefined}
          />
        </Box>
      )}
      {!config?.hasTmdbKey && (
        <Typography variant="caption" color="text.secondary" sx={{ mt: 0.5, display: 'block' }}>
          Nastav TMDB API klíč výše pro aktivaci.
        </Typography>
      )}
      {enrichResult && <Alert severity="success" sx={{ mt: 1 }}>{enrichResult}</Alert>}

      <Divider sx={{ my: 3 }} />

      <Box sx={{ mt: 2 }}>
        <Button variant="contained" onClick={saveApiKeys}>
          Uložit nastavení
        </Button>
        {saved && <Alert severity="success" sx={{ mt: 1 }}>Nastavení uloženo.</Alert>}
      </Box>

      <Divider sx={{ my: 3 }} />

      {/* --- Prenosny disk --- */}
      <Box
        id="portable-export"
        sx={{
          maxWidth: 820,
          border: '1px solid',
          borderColor: 'divider',
          borderRadius: 2.5,
          overflow: 'hidden',
        }}
      >
        <Box sx={{ px: { xs: 2, sm: 2.5 }, py: 2, bgcolor: 'background.paper' }}>
          <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>Přenosný disk</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 650 }}>
            Připrav balík s vybranými filmy nebo sériemi pro přehrávání na jiném počítači.
          </Typography>
        </Box>

        <Box sx={{ p: { xs: 2, sm: 2.5 }, pt: { xs: 2, sm: 2 }, display: 'grid', gap: 2.5 }}>
          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: { xs: '1fr', sm: 'minmax(0, 1fr) auto' },
              alignItems: 'center',
              gap: 1.5,
              p: 2,
              borderRadius: 2,
              bgcolor: 'action.hover',
            }}
          >
            <Box>
              <Typography variant="subtitle2">Obsah balíku</Typography>
              {selectedMediaFileIds.length > 0 ? (
                <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.75, mt: 1 }}>
                  <Chip size="small" label={`${selectedMovieCount} filmů`} />
                  <Chip size="small" label={`${selectedSeasonCount} sérií`} />
                  <Chip size="small" variant="outlined" label={`${selectedMediaFileIds.length} souborů`} />
                </Box>
              ) : (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                  Zatím není nic vybráno.
                </Typography>
              )}
            </Box>
            <Button
              variant="outlined"
              onClick={() => setSelectionOpen(true)}
              disabled={exporting || (exportMovies.length === 0 && exportShows.length === 0)}
              sx={{ width: { xs: '100%', sm: 'auto' }, whiteSpace: 'nowrap' }}
            >
              Vybrat média…
            </Button>
          </Box>

          <Dialog open={selectionOpen} onClose={() => setSelectionOpen(false)} fullWidth maxWidth="md">
        <DialogTitle>Vybrat filmy a seriály</DialogTitle>
        <DialogContent dividers>
          <TextField
            autoFocus
            fullWidth
            size="small"
            label="Hledat v knihovně"
            value={mediaSearch}
            onChange={(event) => setMediaSearch(event.target.value)}
            sx={{ mb: 1.5 }}
          />
          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fill, minmax(110px, 1fr))',
              gap: 1.5,
              maxHeight: 460,
              overflowY: 'auto',
              pr: 0.5,
            }}
          >
            {filteredMovies.map((movie) => (
              <ExportTile
                key={movie.key}
                title={movie.title}
                posterUrl={movie.posterUrl}
                checked={selectedExportItems.has(movie.key)}
                onBodyClick={() => toggleExportItem(movie.key)}
                onToggle={() => toggleExportItem(movie.key)}
              />
            ))}
            {filteredShows.map((show) => {
              const seasonKeys = show.seasons.map((season) => season.key)
              const selectedCount = seasonKeys.filter((key) => selectedExportItems.has(key)).length
              const allSelected = selectedCount === seasonKeys.length
              return (
                <Fragment key={`show:${show.showId}`}>
                  <ExportTile
                    title={show.title}
                    subtitle={`${show.seasons.length} ${show.seasons.length === 1 ? 'série' : 'sérií'}`}
                    posterUrl={show.posterUrl}
                    checked={allSelected}
                    indeterminate={selectedCount > 0 && !allSelected}
                    expanded={expandedShow === show.showId}
                    onBodyClick={() => setExpandedShow((current) => current === show.showId ? null : show.showId)}
                    onToggle={() => setExportItems(seasonKeys, !allSelected)}
                  />
                  {expandedShow === show.showId && (
                    <Box
                      sx={{
                        gridColumn: '1 / -1',
                        p: 1.5,
                        border: '1px solid',
                        borderColor: 'primary.main',
                        borderRadius: 1.5,
                        bgcolor: 'action.hover',
                      }}
                    >
                      <Typography variant="subtitle2" sx={{ mb: 0.5 }}>{show.title} — vyber série</Typography>
                      <FormGroup row>
                        {show.seasons.map((season) => (
                          <FormControlLabel
                            key={season.key}
                            control={
                              <Checkbox
                                size="small"
                                checked={selectedExportItems.has(season.key)}
                                onChange={() => toggleExportItem(season.key)}
                              />
                            }
                            label={`Série ${season.season} (${season.mediaFileIds.length})`}
                          />
                        ))}
                      </FormGroup>
                    </Box>
                  )}
                </Fragment>
              )
            })}
            {filteredMovies.length === 0 && filteredShows.length === 0 && (
              <Typography variant="body2" color="text.secondary" sx={{ gridColumn: '1 / -1', p: 2 }}>
                Žádná položka neodpovídá hledání.
              </Typography>
            )}
          </Box>
        </DialogContent>
        <DialogActions sx={{ justifyContent: 'space-between' }}>
          <Box>
            <Button
              onClick={selectVisibleExportItems}
              disabled={filteredMovies.length === 0 && filteredShows.length === 0}
            >
              Vybrat zobrazené
            </Button>
            <Button onClick={() => setSelectedExportItems(new Set())} disabled={selectedExportItems.size === 0}>
              Zrušit výběr
            </Button>
          </Box>
          <Button variant="contained" onClick={() => setSelectionOpen(false)}>Hotovo</Button>
        </DialogActions>
      </Dialog>

          <Box>
            <Typography variant="subtitle2" sx={{ mb: 1 }}>Cílová složka</Typography>
            <Box
              sx={{
                display: 'grid',
                gridTemplateColumns: { xs: '1fr', sm: 'minmax(0, 1fr) auto' },
                gap: 1,
              }}
            >
              <TextField
                size="small"
                fullWidth
                placeholder="Vyber složku na externím disku"
                value={exportTarget}
                onChange={(e) => setExportTarget(e.target.value)}
                disabled={exporting}
                slotProps={{ htmlInput: { 'aria-label': 'Cílová složka na externím disku' } }}
              />
              <Button
                variant="outlined"
                onClick={browseExportTarget}
                disabled={exporting}
                sx={{ minWidth: 112 }}
              >
                Procházet…
              </Button>
            </Box>
          </Box>

          <Box>
            <Typography variant="subtitle2" sx={{ mb: 1 }}>Volby exportu</Typography>
            <Box
              sx={{
                display: 'grid',
                gridTemplateColumns: { xs: '1fr', sm: 'minmax(0, 1fr) minmax(0, 1fr)' },
                gap: 1.25,
              }}
            >
              <Box sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 2, px: 1.5, py: 1.25 }}>
                <FormControl disabled={exporting}>
                  <FormLabel sx={{ typography: 'caption', color: 'text.secondary' }}>Zdrojové soubory</FormLabel>
                  <RadioGroup
                    row
                    value={exportMode}
                    onChange={(e) => setExportMode(e.target.value as 'copy' | 'move')}
                    sx={{ mt: 0.25, columnGap: 1 }}
                  >
                    <FormControlLabel value="copy" control={<Radio size="small" />} label="Kopírovat" />
                    <FormControlLabel value="move" control={<Radio size="small" />} label="Přesunout" />
                  </RadioGroup>
                </FormControl>
              </Box>
              <Box sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 2, px: 1.5, py: 1.25 }}>
                <FormControlLabel
                  control={<Checkbox checked={includePosters} onChange={(_, value) => setIncludePosters(value)} />}
                  label="Zahrnout plakáty"
                  disabled={exporting}
                  sx={{ m: 0 }}
                />
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', ml: 4 }}>
                  Obaly filmů a seriálů budou dostupné i offline.
                </Typography>
              </Box>
            </Box>
          </Box>

          <Divider />

          <Box
            sx={{
              display: 'flex',
              flexDirection: { xs: 'column-reverse', sm: 'row' },
              alignItems: { xs: 'stretch', sm: 'center' },
              justifyContent: 'space-between',
              gap: 1,
            }}
          >
            <Button variant="text" onClick={importPackage} disabled={exporting}>
              Importovat existující balík…
            </Button>
            <Box sx={{ display: 'flex', flexDirection: { xs: 'column-reverse', sm: 'row' }, gap: 1 }}>
              {exporting && <Button color="error" onClick={cancelExport}>Zrušit</Button>}
              <Button
                variant="contained"
                onClick={startExport}
                disabled={exporting || !exportTarget || selectedMediaFileIds.length === 0}
              >
                {exporting ? 'Exportuji…' : 'Vytvořit balík'}
              </Button>
            </Box>
          </Box>

      {exportStatus?.extended && (
        <Alert severity="info" sx={{ mt: 1.5 }}>Nalezen existující balík — bude rozšířen.</Alert>
      )}

      {exporting && exportStatus.progress && (
        <Box sx={{ mt: 1.5 }}>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 0.5 }}>
            {exportStatus.progress.phase}
            {exportStatus.progress.filesTotal > 0 && ` · ${exportStatus.progress.filesDone}/${exportStatus.progress.filesTotal}`}
            {exportStatus.progress.currentFile && ` · ${exportStatus.progress.currentFile}`}
          </Typography>
          <LinearProgress
            variant={(exportStatus.progress.bytesTotal > 0 || exportStatus.progress.filesTotal > 0) ? 'determinate' : 'indeterminate'}
            value={exportStatus.progress.bytesTotal > 0
              ? (exportStatus.progress.bytesDone / exportStatus.progress.bytesTotal) * 100
              : exportStatus.progress.filesTotal > 0
                ? (exportStatus.progress.filesDone / exportStatus.progress.filesTotal) * 100
                : undefined}
          />
        </Box>
      )}

      {exportStatus?.state === 'completed' && exportStatus.report && (
        <Alert severity={exportStatus.report.failures.length > 0 ? 'warning' : 'success'} sx={{ mt: 1.5 }}>
          <Typography variant="body2">
            Export dokončen · nové {exportStatus.report.newFiles} · již v balíku {exportStatus.report.existingFiles}
            · přeskočené {exportStatus.report.skippedFiles}
          </Typography>
          {exportStatus.report.unmatchedFiles.length > 0 && (
            <details>
              <summary>Nezařazené soubory ({exportStatus.report.unmatchedFiles.length})</summary>
              <List dense>
                {exportStatus.report.unmatchedFiles.map((path) => <ListItem key={path}><ListItemText primary={path} /></ListItem>)}
              </List>
            </details>
          )}
          {exportStatus.report.failures.length > 0 && (
            <details>
              <summary>Selhání ({exportStatus.report.failures.length})</summary>
              <List dense>
                {exportStatus.report.failures.map((failure) => <ListItem key={failure}><ListItemText primary={failure} /></ListItem>)}
              </List>
            </details>
          )}
        </Alert>
      )}
      {exportStatus?.state === 'cancelled' && <Alert severity="info" sx={{ mt: 1.5 }}>Export zrušen mezi soubory.</Alert>}
          {importResult && <Alert severity="success">{importResult}</Alert>}
        </Box>
      </Box>

      <Divider sx={{ my: 3 }} />

      {/* --- About + TMDB atribuce --- */}
      <Typography variant="subtitle1" sx={{ fontWeight: 600, mb: 0.5 }}>O aplikaci</Typography>
      <Typography variant="body2" color="text.secondary">
        Local Stream Player (LSP) · .NET 10 + React + ffmpeg
      </Typography>
      <Typography variant="caption" color="text.disabled" sx={{ mt: 1, display: 'block' }}>
        This product uses the TMDB API but is not endorsed or certified by TMDB.
      </Typography>
    </Box>
  )
}
