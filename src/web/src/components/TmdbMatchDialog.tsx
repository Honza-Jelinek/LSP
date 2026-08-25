import { useEffect, useState } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControl, InputLabel, List, ListItemButton, MenuItem, Select,
  TextField, ToggleButton, ToggleButtonGroup, Typography,
} from '@mui/material'
import { api, type SeasonEpisode, type TmdbCandidate } from '../api'

export type MatchTarget =
  | { kind: 'file'; mediaFileId: number; title: string; isEpisode: boolean; season?: number; episode?: number }
  | { kind: 'show'; showId: number; title: string }

type Props = {
  target: MatchTarget | null
  onClose: () => void
  onSaved: () => void
}

export function TmdbMatchDialog({ target, onClose, onSaved }: Props) {
  const [type, setType] = useState<'movie' | 'tv'>('movie')
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<TmdbCandidate[]>([])
  const [selected, setSelected] = useState<TmdbCandidate | null>(null)
  const [season, setSeason] = useState('1')
  const [episode, setEpisode] = useState('1')
  const [episodes, setEpisodes] = useState<SeasonEpisode[]>([])
  const [searching, setSearching] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Inicializace při otevření.
  useEffect(() => {
    if (!target) return
    setResults([])
    setSelected(null)
    setError(null)
    if (target.kind === 'show') {
      setType('tv')
      setQuery(target.title)
    } else {
      setType(target.isEpisode ? 'tv' : 'movie')
      setQuery(target.title)
      setSeason(String(target.season ?? 1))
      setEpisode(String(target.episode ?? 1))
    }
  }, [target])

  // Načti epizody sezóny pro dropdown (jen film→epizoda po výběru seriálu).
  const isFile = target?.kind === 'file'
  useEffect(() => {
    if (type !== 'tv' || !isFile || !selected) { setEpisodes([]); return }
    const s = Number(season)
    if (!(s > 0)) { setEpisodes([]); return }
    let cancelled = false
    api.getTvSeasonEpisodes(selected.tmdbId, s)
      .then((eps) => { if (!cancelled) setEpisodes(eps) })
      .catch(() => { if (!cancelled) setEpisodes([]) })
    return () => { cancelled = true }
  }, [type, isFile, selected, season])

  const doSearch = async () => {
    if (!query.trim()) return
    setSearching(true)
    setError(null)
    try {
      const r = await api.searchTmdb(query.trim(), type)
      setResults(r)
      if (r.length === 0) setError('Žádné výsledky na TMDB.')
    } catch {
      setError('Hledání selhalo (zkontroluj TMDB klíč).')
    } finally {
      setSearching(false)
    }
  }

  const save = async () => {
    if (!target || !selected) return
    setSaving(true)
    setError(null)
    try {
      if (target.kind === 'show') {
        await api.manualMatchShow(target.showId, selected.tmdbId)
      } else if (type === 'movie') {
        await api.manualMatchFile({ mediaFileId: target.mediaFileId, kind: 'movie', tmdbId: selected.tmdbId, mediaType: 'movie' })
      } else {
        await api.manualMatchFile({
          mediaFileId: target.mediaFileId, kind: 'episode', tmdbId: selected.tmdbId, mediaType: 'tv',
          season: Number(season) || 1, episode: Number(episode) || 1,
        })
      }
      onSaved()
      onClose()
    } catch {
      setError('Uložení selhalo.')
    } finally {
      setSaving(false)
    }
  }

  const canSave = selected !== null && (type === 'movie' || (Number(season) > 0 && Number(episode) > 0))

  return (
    <Dialog open={target !== null} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Najít na TMDB</DialogTitle>
      <DialogContent>
        {isFile && (
          <ToggleButtonGroup
            exclusive
            size="small"
            value={type}
            onChange={(_, v) => { if (v) { setType(v); setResults([]); setSelected(null) } }}
            sx={{ mb: 2 }}
          >
            <ToggleButton value="movie">Film</ToggleButton>
            <ToggleButton value="tv">Epizoda (seriál)</ToggleButton>
          </ToggleButtonGroup>
        )}

        <Box sx={{ display: 'flex', gap: 1, mb: 1 }}>
          <TextField
            size="small" fullWidth autoFocus
            label={type === 'tv' ? 'Název seriálu' : 'Název filmu'}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') void doSearch() }}
          />
          <Button variant="contained" onClick={doSearch} disabled={searching}>
            {searching ? '…' : 'Hledat'}
          </Button>
        </Box>

        {type === 'tv' && isFile && (
          <Box sx={{ display: 'flex', gap: 1, mb: 1 }}>
            <TextField size="small" label="Sezóna" type="number" value={season} onChange={(e) => setSeason(e.target.value)} sx={{ width: 110 }} />
            {episodes.length > 0 ? (
              <FormControl size="small" sx={{ minWidth: 220, flex: 1 }}>
                <InputLabel>Epizoda</InputLabel>
                <Select label="Epizoda" value={episode} onChange={(e) => setEpisode(String(e.target.value))}>
                  {episodes.map((ep) => (
                    <MenuItem key={ep.number} value={String(ep.number)}>
                      {`E${String(ep.number).padStart(2, '0')}${ep.title ? ` — ${ep.title}` : ''}`}
                    </MenuItem>
                  ))}
                </Select>
              </FormControl>
            ) : (
              <TextField size="small" label="Epizoda" type="number" value={episode} onChange={(e) => setEpisode(e.target.value)} sx={{ width: 110 }} />
            )}
          </Box>
        )}

        {error && <Typography color="error" variant="body2" sx={{ my: 1 }}>{error}</Typography>}

        <List dense sx={{ maxHeight: 320, overflow: 'auto' }}>
          {results.map((r) => (
            <ListItemButton
              key={r.tmdbId}
              selected={selected?.tmdbId === r.tmdbId}
              onClick={() => setSelected(r)}
              sx={{ alignItems: 'flex-start', gap: 1.5 }}
            >
              <Box
                component="img"
                src={r.posterUrl ?? ''}
                sx={{ width: 46, height: 69, objectFit: 'cover', borderRadius: 0.5, bgcolor: 'rgba(255,255,255,0.08)', flexShrink: 0 }}
              />
              <Box sx={{ minWidth: 0 }}>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>
                  {r.title} {r.year ? `(${r.year})` : ''}
                </Typography>
                <Typography variant="caption" color="text.secondary" sx={{
                  display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
                }}>
                  {r.overview}
                </Typography>
              </Box>
            </ListItemButton>
          ))}
        </List>

        <Typography variant="caption" color="text.disabled" sx={{ mt: 1, display: 'block' }}>
          Data z TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB.
        </Typography>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Zrušit</Button>
        <Button variant="contained" onClick={save} disabled={!canSave || saving}>
          {saving ? 'Ukládám…' : 'Přiřadit'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
