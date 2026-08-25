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
import LinkOffIcon from '@mui/icons-material/LinkOff'
import { api } from '../api'
import { useLibrary } from '../library'
import { useTmdbMatch } from '../components/useTmdbMatch'

/** Mezistránka filmu (jako ShowDetail): všechna napojená média, přehrání i odpojení zdroje. */
export function MovieDetail() {
  const { mediaFileId } = useParams()
  const navigate = useNavigate()
  const tmdb = useTmdbMatch()
  const { library, reload } = useLibrary()

  const fid = Number(mediaFileId)
  const movie = library?.movies.find(
    (m) => m.mediaFileId === fid || m.sources.some((s) => s.mediaFileId === fid),
  )

  if (!library) return <Typography sx={{ p: 3 }} color="text.secondary">Načítám…</Typography>
  if (!movie)
    return (
      <Box sx={{ p: 3 }}>
        <Typography color="text.secondary">Film nenalezen.</Typography>
        <Button onClick={() => navigate('/movies')} sx={{ mt: 1 }}>Zpět</Button>
      </Box>
    )

  const watch = (id: number) =>
    navigate(`/watch/${id}`, { state: { title: movie.title } })

  const unlink = async (id: number) => {
    try {
      await api.resetMatchFile(id)
      await reload()
    } catch { /* ignore */ }
  }

  return (
    <Box sx={{ minHeight: '100vh' }}>
      <Box
        sx={{
          p: 3,
          pb: 2,
          display: 'flex',
          gap: 2.5,
          alignItems: 'flex-start',
          background: 'linear-gradient(160deg, rgba(229,9,20,0.14), transparent 70%)',
        }}
      >
        <IconButton onClick={() => navigate(-1)} sx={{ mt: 0.5 }}>
          <ArrowBackIcon />
        </IconButton>
        {movie.posterUrl && (
          <Box
            component="img"
            src={movie.posterUrl}
            alt={movie.title}
            sx={{ width: 110, borderRadius: 1.5, flexShrink: 0 }}
          />
        )}
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="h4" sx={{ fontWeight: 700 }}>
            {movie.title} {movie.year ? `(${movie.year})` : ''}
          </Typography>
          {movie.genres && (
            <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.6, mt: 1 }}>
              {movie.genres.split(',').map((g) => g.trim()).filter(Boolean).map((g) => (
                <Chip key={g} label={g} size="small" variant="outlined" />
              ))}
            </Box>
          )}
          <Button
            variant="contained"
            startIcon={<PlayArrowIcon />}
            onClick={() => watch(movie.mediaFileId)}
            sx={{ mt: 2 }}
          >
            Přehrát
          </Button>
        </Box>
      </Box>

      <Typography variant="h6" sx={{ px: 3, mt: 1 }}>
        Zdroje ({movie.sources.length})
      </Typography>
      <List sx={{ px: 1 }}>
        {movie.sources.map((s) => (
          <ListItemButton
            key={s.mediaFileId}
            onClick={() => watch(s.mediaFileId)}
            onContextMenu={(e) => tmdb.open(e, {
              kind: 'file', mediaFileId: s.mediaFileId, title: movie.title, isEpisode: false,
            })}
            sx={{ borderRadius: 1, mb: 0.4 }}
          >
            <ListItemIcon sx={{ minWidth: 36 }}>
              <PlayArrowIcon sx={{ color: 'text.secondary' }} />
            </ListItemIcon>
            <ListItemText
              primary={s.fileName}
              slotProps={{ primary: { noWrap: true, sx: { fontSize: '0.85rem' } } }}
            />
            {movie.sources.length > 1 && (
              <Button
                size="small"
                color="inherit"
                startIcon={<LinkOffIcon />}
                onClick={(e) => { e.stopPropagation(); void unlink(s.mediaFileId) }}
                sx={{ ml: 1, color: 'text.secondary', flexShrink: 0 }}
              >
                Odpojit
              </Button>
            )}
          </ListItemButton>
        ))}
      </List>
      {tmdb.element}
    </Box>
  )
}
