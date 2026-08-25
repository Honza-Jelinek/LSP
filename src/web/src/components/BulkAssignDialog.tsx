import { useEffect, useMemo, useState } from 'react'
import {
  Autocomplete, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  List, ListItemButton, TextField, ToggleButton, ToggleButtonGroup, Typography,
} from '@mui/material'
import { api, type TvEpisode, type TmdbCandidate } from '../api'

// Minimální tvar souboru pro dialog — ReviewItem mu strukturálně vyhovuje.
export type AssignFile = {
  mediaFileId: number
  title: string
  fileName: string
  season?: number | null
  episode?: number | null
}

type Props = {
  files: AssignFile[]   // vybrané soubory (media file k přiřazení)
  open: boolean
  initialType?: 'movie' | 'tv'  // výchozí režim (context menu: epizoda → 'tv')
  onClose: () => void
  onSaved: () => void
}

type Row = { season: string; episode: string }
type Mode = 'movie' | 'tv' | 'lazy'

const epLabel = (e: TvEpisode) =>
  `S${String(e.season).padStart(2, '0')}E${String(e.number).padStart(2, '0')}${e.title ? ` — ${e.title}` : ''}`

export function BulkAssignDialog({ files, open, initialType, onClose, onSaved }: Props) {
  const single = files.length === 1
  const [type, setType] = useState<Mode>('tv')
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<TmdbCandidate[]>([])
  const [picked, setPicked] = useState<TmdbCandidate | null>(null)
  const [rows, setRows] = useState<Record<number, Row>>({})
  const [allEps, setAllEps] = useState<TvEpisode[]>([])
  const [openRow, setOpenRow] = useState<number | null>(null)
  const [searching, setSearching] = useState(false)
  const [saving, setSaving] = useState(false)
  const [progress, setProgress] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const doSearch = async (q: string) => {
    if (!q.trim()) return
    setSearching(true)
    setError(null)
    try {
      const r = await api.searchTmdb(q.trim(), type === 'movie' ? 'movie' : 'tv')
      setResults(r)
      if (r.length === 0) setError('Žádné výsledky na TMDB.')
    } catch {
      setError('Hledání selhalo (zkontroluj TMDB klíč).')
    } finally {
      setSearching(false)
    }
  }

  // Inicializace při otevření: prefill názvu + autosearch (single).
  useEffect(() => {
    if (!open) return
    const initType: Mode = initialType ?? (single ? 'movie' : 'tv')
    const initQuery = files[0]?.title ?? ''
    setType(initType)
    setQuery(initQuery)
    setResults([])
    setPicked(null)
    setAllEps([])
    setOpenRow(null)
    setError(null)
    setProgress(null)
    const init: Record<number, Row> = {}
    for (const f of files) init[f.mediaFileId] = { season: String(f.season ?? 1), episode: String(f.episode ?? 1) }
    setRows(init)
    if (single && initQuery.trim()) void doSearchAs(initType, initQuery)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, files, single, initialType])

  // Po výběru seriálu načti VŠECHNY epizody napříč sezónami (pro Autocomplete).
  useEffect(() => {
    if (type !== 'tv' || !picked) { setAllEps([]); return }
    let cancelled = false
    api.getTvAllEpisodes(picked.tmdbId)
      .then((eps) => { if (!cancelled) setAllEps(eps) })
      .catch(() => { if (!cancelled) setAllEps([]) })
    return () => { cancelled = true }
  }, [type, picked])

  const changeType = (v: Mode) => {
    setType(v)
    setResults([])
    setPicked(null)
    setError(null)
    if (query.trim()) void doSearchAs(v, query)
  }

  // doSearch s explicitním typem (toggle mění type až po renderu). Lazy hledá jako 'tv' (měkké napojení).
  const doSearchAs = async (mode: Mode, q: string) => {
    if (!q.trim()) return
    setSearching(true)
    setError(null)
    try {
      const r = await api.searchTmdb(q.trim(), mode === 'movie' ? 'movie' : 'tv')
      setResults(r)
      if (r.length === 0) setError('Žádné výsledky na TMDB.')
    } catch {
      setError('Hledání selhalo (zkontroluj TMDB klíč).')
    } finally {
      setSearching(false)
    }
  }

  const setRow = (id: number, patch: Partial<Row>) =>
    setRows((prev) => ({ ...prev, [id]: { ...prev[id], ...patch } }))

  const save = async () => {
    setSaving(true)
    setError(null)

    // Single film: přiřaď jako film.
    if (single && type === 'movie') {
      if (!picked) { setSaving(false); return }
      try {
        await api.manualMatchFile({ mediaFileId: files[0].mediaFileId, kind: 'movie', tmdbId: picked.tmdbId, mediaType: 'movie' })
        setSaving(false)
        onSaved()
        onClose()
      } catch {
        setSaving(false)
        setError('Uložení selhalo.')
      }
      return
    }

    if (type === 'tv' && !picked) { setSaving(false); return }
    // Lazy: název série = vybraný TMDB titul, jinak napsaný text. Volitelně soft tmdbId (banner + název).
    const lazyName = picked?.title ?? query.trim()
    const softTmdbId = type === 'lazy' ? (picked?.tmdbId ?? 0) : 0
    if (type === 'lazy' && !lazyName) { setSaving(false); return }

    // Epizody (TMDB nebo lazy): sekvenčně (server cachuje sezónu → min. TMDB callů).
    const failed: string[] = []
    for (let i = 0; i < files.length; i++) {
      const f = files[i]
      const row = rows[f.mediaFileId]
      setProgress(`${i + 1}/${files.length}`)
      try {
        if (type === 'lazy') {
          // Bez S/E — server očísluje automaticky (S1, další volné číslo). softTmdbId>0 = banner + název.
          await api.manualMatchFile({
            mediaFileId: f.mediaFileId, kind: 'lazy-episode', tmdbId: softTmdbId,
            mediaType: softTmdbId > 0 ? 'tv' : 'none', showTitle: lazyName,
          })
        } else {
          await api.manualMatchFile({
            mediaFileId: f.mediaFileId, kind: 'episode', tmdbId: picked!.tmdbId, mediaType: 'tv',
            season: Number(row.season) || 1, episode: Number(row.episode) || 1,
          })
        }
      } catch {
        failed.push(f.title)
      }
    }
    setSaving(false)
    setProgress(null)
    if (failed.length > 0) {
      setError(`Nepodařilo se přiřadit: ${failed.join(', ')}`)
      onSaved() // refresh i tak — část mohla projít
    } else {
      onSaved()
      onClose()
    }
  }

  const title = single
    ? (type === 'movie' ? 'Přiřadit film' : type === 'lazy' ? 'Lazy série' : 'Přiřadit epizodu')
    : type === 'lazy' ? `Lazy série (${files.length})` : `Přiřadit vybrané seriálu (${files.length})`

  const saveDisabled = saving
    || (type === 'lazy' ? !(picked || query.trim()) : !picked)
  const saveLabel = type === 'lazy'
    ? 'Vytvořit sérii'
    : type === 'movie' ? 'Přiřadit' : 'Přiřadit vše'

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle>{title}</DialogTitle>
      <DialogContent>
        <ToggleButtonGroup
          exclusive size="small" value={type}
          onChange={(_, v) => { if (v) changeType(v) }}
          sx={{ mb: 2 }}
        >
          {single && <ToggleButton value="movie">Film</ToggleButton>}
          <ToggleButton value="tv">{single ? 'Epizoda (seriál)' : 'Seriál'}</ToggleButton>
          <ToggleButton value="lazy">Lazy série</ToggleButton>
        </ToggleButtonGroup>

        <Box sx={{ display: 'flex', gap: 1, mb: type === 'lazy' ? 0 : 1 }}>
          <TextField
            size="small" fullWidth autoFocus
            label={type === 'tv' ? 'Název seriálu' : type === 'lazy' ? 'Název série' : 'Název filmu'}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') void doSearch(query) }}
          />
          <Button variant="contained" onClick={() => doSearch(query)} disabled={searching}>
            {searching ? '…' : 'Hledat'}
          </Button>
        </Box>
        {type === 'lazy' && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1, mt: 0.5 }}>
            Vyhledej seriál pro banner + název (měkké napojení), nebo nech nenapojený. Epizody z názvů souborů, číslování automatické.
          </Typography>
        )}

        {error && <Typography color="error" variant="body2" sx={{ my: 1 }}>{error}</Typography>}

        {!picked && (
          <List dense sx={{ maxHeight: 240, overflow: 'auto' }}>
            {results.map((r) => (
              <ListItemButton key={r.tmdbId} onClick={() => setPicked(r)} sx={{ alignItems: 'flex-start', gap: 1.5 }}>
                <Box
                  component="img" src={r.posterUrl ?? ''}
                  sx={{ width: 46, height: 69, objectFit: 'cover', borderRadius: 0.5, bgcolor: 'rgba(255,255,255,0.08)', flexShrink: 0 }}
                />
                <Box sx={{ minWidth: 0 }}>
                  <Typography variant="body2" sx={{ fontWeight: 600 }}>{r.title} {r.year ? `(${r.year})` : ''}</Typography>
                  <Typography variant="caption" color="text.secondary" sx={{
                    display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
                  }}>{r.overview}</Typography>
                </Box>
              </ListItemButton>
            ))}
          </List>
        )}

        {picked && (
          <Box>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 1 }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {type === 'movie' ? 'Film' : type === 'lazy' ? 'Série' : 'Seriál'}: {picked.title} {picked.year ? `(${picked.year})` : ''}
              </Typography>
              <Button size="small" onClick={() => setPicked(null)}>Změnit</Button>
            </Box>

            {type === 'tv' && (
              <List dense sx={{ maxHeight: 340, overflow: 'auto' }}>
                {files.map((f) => (
                  <EpisodeRow
                    key={f.mediaFileId}
                    file={f}
                    row={rows[f.mediaFileId]}
                    allEps={allEps}
                    highlighted={openRow === f.mediaFileId}
                    onOpen={() => setOpenRow(f.mediaFileId)}
                    onCloseRow={() => setOpenRow(null)}
                    onChange={(patch) => setRow(f.mediaFileId, patch)}
                  />
                ))}
              </List>
            )}
          </Box>
        )}

        {type === 'lazy' && (
          <List dense sx={{ maxHeight: 340, overflow: 'auto' }}>
            {files.map((f, i) => (
              <Box key={f.mediaFileId} sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.4, px: 0.5 }}>
                <Typography variant="caption" color="text.secondary" sx={{ width: 24, textAlign: 'right', flexShrink: 0 }}>
                  {i + 1}.
                </Typography>
                <Typography variant="caption" noWrap title={f.fileName}>
                  {f.fileName || f.title}
                </Typography>
              </Box>
            ))}
          </List>
        )}

        <Typography variant="caption" color="text.disabled" sx={{ mt: 1, display: 'block' }}>
          Data z TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB.
        </Typography>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Zrušit</Button>
        <Button variant="contained" onClick={save} disabled={saveDisabled}>
          {saving ? `Ukládám… ${progress ?? ''}` : saveLabel}
        </Button>
      </DialogActions>
    </Dialog>
  )
}

type RowProps = {
  file: AssignFile
  row: Row | undefined
  allEps: TvEpisode[]
  highlighted: boolean
  onOpen: () => void
  onCloseRow: () => void
  onChange: (patch: Partial<Row>) => void
}

function EpisodeRow({ file, row, allEps, highlighted, onOpen, onCloseRow, onChange }: RowProps) {
  const value = useMemo(
    () => allEps.find((e) => e.season === Number(row?.season) && e.number === Number(row?.episode)) ?? null,
    [allEps, row?.season, row?.episode],
  )

  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: '1fr 360px',
        alignItems: 'center',
        gap: 1,
        py: 0.5,
        px: 0.5,
        borderRadius: 1,
        bgcolor: highlighted ? 'action.selected' : 'transparent',
        transition: 'background-color 0.15s',
      }}
    >
      <Typography variant="caption" noWrap title={file.fileName}>
        {file.fileName || file.title}
      </Typography>

      {allEps.length > 0 ? (
        <Autocomplete
          size="small"
          options={allEps}
          value={value}
          openOnFocus
          getOptionLabel={epLabel}
          isOptionEqualToValue={(o, v) => o.season === v.season && o.number === v.number}
          onChange={(_, v) => { if (v) onChange({ season: String(v.season), episode: String(v.number) }) }}
          onOpen={onOpen}
          onClose={onCloseRow}
          renderInput={(params) => <TextField {...params} label="Epizoda" />}
          sx={{ width: '100%' }}
        />
      ) : (
        // Fallback (lazy série / TMDB epizody se nenačetly): ruční S/E.
        <Box sx={{ display: 'flex', gap: 1 }}>
          <TextField
            size="small" label="S" type="number" value={row?.season ?? '1'}
            onChange={(e) => onChange({ season: e.target.value })} sx={{ width: '50%' }}
          />
          <TextField
            size="small" label="E" type="number" value={row?.episode ?? '1'}
            onChange={(e) => onChange({ episode: e.target.value })} sx={{ width: '50%' }}
          />
        </Box>
      )}
    </Box>
  )
}
