import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Box, Divider, Menu, MenuItem, Typography } from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlined'
import SaveAltIcon from '@mui/icons-material/SaveAlt'
import { MediaCard } from '../components/MediaCard'
import { useTmdbMatch } from '../components/useTmdbMatch'
import { api, type HomeItem, type HomeRow } from '../api'
import { portableExportPath } from '../portableExport'

const rowSx = {
  display: 'flex',
  gap: 1.5,
  overflowX: 'auto',
  pb: 1,
  scrollSnapType: 'x proximity',
  '&::-webkit-scrollbar': { height: 6 },
  '&::-webkit-scrollbar-thumb': { bgcolor: 'rgba(255,255,255,0.15)', borderRadius: 3 },
} as const

export function Home() {
  const navigate = useNavigate()
  const tmdb = useTmdbMatch()
  const [rows, setRows] = useState<HomeRow[] | null>(null)
  const [continueMenu, setContinueMenu] = useState<{ x: number; y: number; item: HomeItem } | null>(null)

  const reload = () => { void api.getHome().then(setRows).catch(() => setRows([])) }

  useEffect(reload, [])

  const openItem = (item: HomeItem, rowKey: string) => {
    if (item.kind === 'show' && item.showId !== null) {
      navigate(`/show/${item.showId}`)
      return
    }
    if (item.mediaFileId === null) return
    // Film → detail (výběr zdroje); "Pokračovat" a epizody rovnou přehrát.
    if (item.kind === 'movie' && rowKey !== 'continue-watching') {
      navigate(`/movie/${item.mediaFileId}`)
    } else {
      navigate(`/watch/${item.mediaFileId}`, { state: { title: item.title } })
    }
  }

  const contextMenuFor = (item: HomeItem) => {
    if (item.kind === 'show' && item.showId !== null) {
      return (e: React.MouseEvent) => tmdb.open(e, { kind: 'show', showId: item.showId!, title: item.title })
    }
    if (item.kind === 'movie' && item.mediaFileId !== null) {
      return (e: React.MouseEvent) =>
        tmdb.open(e, { kind: 'file', mediaFileId: item.mediaFileId!, title: item.title, isEpisode: false })
    }
    return undefined
  }

  const openContinueMenu = (e: React.MouseEvent, item: HomeItem) => {
    e.preventDefault()
    setContinueMenu({ x: e.clientX, y: e.clientY, item })
  }

  const playFromStart = (item: HomeItem) => {
    if (item.mediaFileId === null) return
    navigate(`/watch/${item.mediaFileId}`, { state: { title: item.title, fromStart: true } })
  }

  const openDetail = (item: HomeItem) => {
    if (item.kind === 'movie' && item.mediaFileId !== null) navigate(`/movie/${item.mediaFileId}`)
    else if (item.showId !== null) navigate(`/show/${item.showId}`)
  }

  const removeFromContinue = async (item: HomeItem) => {
    if (item.kind === 'movie' && item.mediaFileId !== null) await api.deleteProgress(item.mediaFileId)
    else if (item.showId !== null) await api.deleteShowProgress(item.showId)
    reload()
  }

  const exportItem = (item: HomeItem) => {
    if (item.kind === 'movie' && item.mediaFileId !== null) {
      navigate(portableExportPath({ kind: 'movie', mediaFileId: item.mediaFileId }))
    } else if (item.showId !== null) {
      navigate(portableExportPath({ kind: 'show', showId: item.showId }))
    }
  }

  return (
    <Box sx={{ p: 3 }}>
      {rows?.map((row) => (
        <Box key={row.key} sx={{ mb: 3.5 }}>
          <Typography variant="h6" sx={{ mb: 1.2 }}>{row.title}</Typography>
          <Box sx={rowSx}>
            {row.items.map((item, i) => (
              <Box key={`${row.key}-${i}`} sx={{ flex: '0 0 150px', scrollSnapAlign: 'start' }}>
                <MediaCard
                  title={item.title}
                  subtitle={item.subtitle ?? undefined}
                  badge={item.badge ?? undefined}
                  posterUrl={item.posterUrl}
                  progress={item.progress ?? undefined}
                  onClick={() => openItem(item, row.key)}
                  onContextMenu={
                    row.key === 'continue-watching'
                      ? (e) => openContinueMenu(e, item)
                      : contextMenuFor(item)
                  }
                />
              </Box>
            ))}
          </Box>
        </Box>
      ))}

      {rows === null && <Typography color="text.secondary" sx={{ mt: 4 }}>Načítám knihovnu…</Typography>}
      {tmdb.element}

      <Menu
        open={continueMenu !== null}
        onClose={() => setContinueMenu(null)}
        anchorReference="anchorPosition"
        anchorPosition={continueMenu ? { top: continueMenu.y, left: continueMenu.x } : undefined}
      >
        <MenuItem onClick={() => { playFromStart(continueMenu!.item); setContinueMenu(null) }}>
          <PlayArrowIcon fontSize="small" sx={{ mr: 1 }} />
          Přehrát od začátku
        </MenuItem>
        <MenuItem onClick={() => { openDetail(continueMenu!.item); setContinueMenu(null) }}>
          <InfoOutlinedIcon fontSize="small" sx={{ mr: 1 }} />
          Detail
        </MenuItem>
        <MenuItem onClick={() => { exportItem(continueMenu!.item); setContinueMenu(null) }}>
          <SaveAltIcon fontSize="small" sx={{ mr: 1 }} />
          Exportovat na disk…
        </MenuItem>
        <Divider />
        <MenuItem
          sx={{ color: 'error.main' }}
          onClick={() => { const item = continueMenu!.item; setContinueMenu(null); void removeFromContinue(item) }}
        >
          <DeleteOutlineIcon fontSize="small" sx={{ mr: 1 }} />
          Odstranit
        </MenuItem>
      </Menu>
    </Box>
  )
}
