import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Box, Chip, Grid, TextField, Typography } from '@mui/material'
import { useLibrary } from '../library'
import { MediaCard } from '../components/MediaCard'
import { useTmdbMatch } from '../components/useTmdbMatch'

function allGenres(items: Array<{ genres: string | null }>): string[] {
  const set = new Set<string>()
  for (const item of items) {
    if (!item.genres) continue
    for (const g of item.genres.split(',')) {
      const t = g.trim()
      if (t) set.add(t)
    }
  }
  return [...set].sort((a, b) => a.localeCompare(b, 'cs'))
}

export function Movies() {
  const { library } = useLibrary()
  const navigate = useNavigate()
  const tmdb = useTmdbMatch()
  const [query, setQuery] = useState('')
  const [genre, setGenre] = useState<string | null>(null)
  const q = query.trim().toLowerCase()

  const allMovies = library?.movies ?? []
  const genres = useMemo(() => allGenres(allMovies), [allMovies])

  const movies = useMemo(
    () => allMovies.filter((m) => {
      if (q && !m.title.toLowerCase().includes(q)) return false
      if (genre && (!m.genres || !m.genres.split(',').some((g) => g.trim() === genre))) return false
      return true
    }),
    [allMovies, q, genre],
  )

  return (
    <Box sx={{ p: 3 }}>
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 2, mb: 1.5, flexWrap: 'wrap' }}>
        <Typography variant="h5">Filmy</Typography>
        <TextField
          size="small"
          placeholder="Hledat film…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          sx={{ maxWidth: 360, flex: 1 }}
        />
      </Box>

      {genres.length > 0 && (
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.6, mb: 2 }}>
          <Chip
            label="Všechny"
            size="small"
            color={genre === null ? 'primary' : 'default'}
            variant={genre === null ? 'filled' : 'outlined'}
            onClick={() => setGenre(null)}
          />
          {genres.map((g) => (
            <Chip
              key={g}
              label={g}
              size="small"
              color={genre === g ? 'primary' : 'default'}
              variant={genre === g ? 'filled' : 'outlined'}
              onClick={() => setGenre(genre === g ? null : g)}
            />
          ))}
        </Box>
      )}

      <Grid container spacing={1.5}>
        {movies.map((m) => (
          <Grid key={m.mediaFileId} size={{ xs: 4, sm: 3, md: 2, lg: 2 }}>
            <MediaCard
              title={m.sources.length > 1 ? `${m.title} (${m.sources.length})` : m.title}
              subtitle={m.genres ?? (m.year ? String(m.year) : undefined)}
              posterUrl={m.posterUrl}
              onClick={() => navigate(`/movie/${m.mediaFileId}`)}
              onContextMenu={(e) => tmdb.open(e, { kind: 'file', mediaFileId: m.mediaFileId, title: m.title, isEpisode: false })}
            />
          </Grid>
        ))}
      </Grid>
      {library && movies.length === 0 && (
        <Typography color="text.secondary" sx={{ mt: 3 }}>
          Žádné filmy{q ? ` pro „${query}"` : ''}{genre ? ` v žánru „${genre}"` : ''}.
        </Typography>
      )}
      {tmdb.element}
    </Box>
  )
}
