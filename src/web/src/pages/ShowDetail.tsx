import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  Box,
  Button,
  Chip,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Typography,
} from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import { useLibrary } from '../library'
import { useTmdbMatch } from '../components/useTmdbMatch'

export function ShowDetail() {
  const { id } = useParams()
  const navigate = useNavigate()
  const tmdb = useTmdbMatch()
  const { library } = useLibrary()
  const [activeSeason, setActiveSeason] = useState<number | null>(null)

  const show = library?.shows.find((s) => s.id === Number(id))

  if (!library) return <Typography sx={{ p: 3 }} color="text.secondary">Načítám…</Typography>
  if (!show)
    return (
      <Box sx={{ p: 3 }}>
        <Typography color="text.secondary">Seriál nenalezen.</Typography>
        <Button onClick={() => navigate('/')} sx={{ mt: 1 }}>Zpět</Button>
      </Box>
    )

  const season = show.seasons.find((s) => s.season === activeSeason) ?? show.seasons[0]
  const totalEpisodes = show.seasons.reduce((n, s) => n + s.episodes.length, 0)

  const watch = (mediaFileId: number, label: string) =>
    navigate(`/watch/${mediaFileId}`, { state: { title: `${show.title} – ${label}` } })

  return (
    <Box sx={{ minHeight: '100vh' }}>
      <Box
        sx={{
          p: 3,
          pb: 2,
          background: 'linear-gradient(160deg, rgba(229,9,20,0.14), transparent 70%)',
        }}
      >
        <IconButton onClick={() => navigate(-1)} sx={{ mr: 1 }}>
          <ArrowBackIcon />
        </IconButton>
        <Typography variant="h4" sx={{ mt: 1, fontWeight: 700 }}>{show.title}</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
          {show.seasons.length} {show.seasons.length === 1 ? 'sezóna' : 'sezón'} · {totalEpisodes} epizod
        </Typography>
      </Box>

      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.8, px: 3, mb: 1.5 }}>
        {show.seasons.map((s) => (
          <Chip
            key={s.season}
            label={`Sezóna ${s.season}`}
            color={s.season === season.season ? 'primary' : 'default'}
            variant={s.season === season.season ? 'filled' : 'outlined'}
            onClick={() => setActiveSeason(s.season)}
          />
        ))}
      </Box>

      <List sx={{ px: 1 }}>
        {season.episodes.map((e) => {
          const label = `S${String(e.season).padStart(2, '0')}E${String(e.number).padStart(2, '0')}`
          return (
            <ListItemButton
              key={e.mediaFileId}
              onClick={() => watch(e.mediaFileId, label)}
              onContextMenu={(ev) => tmdb.open(ev, {
                kind: 'file', mediaFileId: e.mediaFileId,
                title: show.title, isEpisode: true, season: e.season, episode: e.number,
              })}
              sx={{ borderRadius: 1, mb: 0.4 }}
            >
              <ListItemIcon sx={{ minWidth: 36 }}>
                <Typography variant="body1" sx={{ fontWeight: 700, color: 'text.secondary' }}>
                  {e.number}
                </Typography>
              </ListItemIcon>
              <ListItemText
                primary={e.title ?? e.fileName.replace(/\.[^.]+$/, '')}
                secondary={e.fileName}
                slotProps={{
                  secondary: {
                    noWrap: true,
                    sx: { fontSize: '0.75rem', color: 'text.disabled' },
                  },
                }}
              />
              <PlayArrowIcon sx={{ color: 'text.secondary', ml: 1 }} />
            </ListItemButton>
          )
        })}
      </List>
      {tmdb.element}
    </Box>
  )
}
