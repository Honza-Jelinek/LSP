import { useEffect, useRef, useState } from 'react'
import {
  Box, Button, Checkbox, Chip, List, ListItemButton, ListItemText, Tabs, Tab, Typography,
} from '@mui/material'
import { api, type ReviewItem, type ReviewQueue } from '../api'
import { useLibrary } from '../library'
import { TmdbMatchDialog, type MatchTarget } from '../components/TmdbMatchDialog'
import { BulkAssignDialog } from '../components/BulkAssignDialog'

export function ReviewQueue() {
  const { reload } = useLibrary()
  const [queue, setQueue] = useState<ReviewQueue | null>(null)
  const [tab, setTab] = useState(0)
  const [dialogTarget, setDialogTarget] = useState<MatchTarget | null>(null)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [bulkFiles, setBulkFiles] = useState<ReviewItem[]>([])
  const [bulkOpen, setBulkOpen] = useState(false)

  // Shift-rozsah + drag-paint stav (refs = nepotřebují rerender).
  const lastIndexRef = useRef<number>(-1)
  const paintingRef = useRef(false)
  const paintValueRef = useRef(false)

  const load = async () => {
    try {
      setQueue(await api.getReviewQueue())
    } catch { /* ignore */ }
  }

  useEffect(() => { void load() }, [])

  // Ukonči drag-paint při puštění tlačítka kdekoliv.
  useEffect(() => {
    const stop = () => { paintingRef.current = false }
    window.addEventListener('mouseup', stop)
    return () => window.removeEventListener('mouseup', stop)
  }, [])

  const items = tab === 0
    ? (queue?.shows ?? [])
    : (queue?.movies ?? [])

  const isMoviesTab = tab === 1

  // Klik na položku: seriál → show dialog; soubor → bulk dialog pro 1 item (sjednocené UI).
  const openItem = (item: ReviewItem) => {
    if (item.kind === 'show') setDialogTarget({ kind: 'show', showId: item.id, title: item.title })
    else { setBulkFiles([item]); setBulkOpen(true) }
  }

  const setRange = (fromIdx: number, toIdx: number, value: boolean) => {
    const lo = Math.min(fromIdx, toIdx)
    const hi = Math.max(fromIdx, toIdx)
    setSelected((prev) => {
      const next = new Set(prev)
      for (let i = lo; i <= hi; i++) {
        const id = (items[i] as ReviewItem).mediaFileId
        if (value) next.add(id); else next.delete(id)
      }
      return next
    })
  }

  const handleCheckboxMouseDown = (e: React.MouseEvent, idx: number, id: number) => {
    e.stopPropagation()
    e.preventDefault() // zabrání text-selekci při dragu

    if (e.shiftKey && lastIndexRef.current >= 0) {
      const value = !selected.has(id)
      setRange(lastIndexRef.current, idx, value)
      lastIndexRef.current = idx
      return
    }

    // Normal toggle + spusť drag-paint.
    const newValue = !selected.has(id)
    paintValueRef.current = newValue
    paintingRef.current = true
    lastIndexRef.current = idx
    setSelected((prev) => {
      const next = new Set(prev)
      if (newValue) next.add(id); else next.delete(id)
      return next
    })
  }

  const handleRowMouseEnter = (id: number) => {
    if (!paintingRef.current) return
    setSelected((prev) => {
      const next = new Set(prev)
      if (paintValueRef.current) next.add(id); else next.delete(id)
      return next
    })
  }

  const allSelected = isMoviesTab && items.length > 0 && items.every((i) => selected.has(i.mediaFileId))
  const toggleAll = () =>
    setSelected(allSelected ? new Set() : new Set(items.map((i) => i.mediaFileId)))

  const selectedFiles = (queue?.movies ?? []).filter((i) => selected.has(i.mediaFileId))

  const openBulk = () => { setBulkFiles(selectedFiles); setBulkOpen(true) }

  const afterSaved = () => { void reload(); void load(); setSelected(new Set()) }

  const keepWithoutTmdb = async (showId: number) => {
    try { await api.lazyShow(showId); afterSaved() } catch { /* ignore */ }
  }

  return (
    <Box sx={{ p: 3 }}>
      <Typography variant="h5" sx={{ mb: 1 }}>Ke kontrole</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Položky, které nemají přiřazené TMDB metadata. Kliknutím na položku otevři TMDB dialog.
      </Typography>

      <Tabs value={tab} onChange={(_, v) => { setTab(v); setSelected(new Set()); lastIndexRef.current = -1 }} sx={{ mb: 2 }}>
        <Tab label={`Seriály (${queue?.shows.length ?? 0})`} />
        <Tab label={`Filmy (${queue?.movies.length ?? 0})`} />
      </Tabs>

      {isMoviesTab && items.length > 0 && (
        <Box sx={{
          position: 'sticky', top: 0, zIndex: 2,
          display: 'flex', alignItems: 'center', gap: 1,
          py: 1, mb: 1, bgcolor: 'background.default',
        }}>
          <Button size="small" onClick={toggleAll}>
            {allSelected ? 'Zrušit výběr' : 'Vybrat vše'}
          </Button>
          <Button
            size="small" variant="contained" disabled={selected.size === 0}
            onClick={openBulk}
          >
            Přiřadit vybrané seriálu ({selected.size})
          </Button>
        </Box>
      )}

      {items.length === 0 ? (
        <Typography color="text.secondary">Žádné položky ke kontrole.</Typography>
      ) : (
        <List sx={{ userSelect: 'none' }}>
          {items.map((item, idx) => (
            <ListItemButton
              key={`${item.kind}-${item.id}`}
              onClick={() => { if (!paintingRef.current) openItem(item) }}
              onContextMenu={(e) => { e.preventDefault(); openItem(item) }}
              onMouseEnter={() => handleRowMouseEnter(item.mediaFileId)}
              sx={{ borderRadius: 1, mb: 0.5 }}
            >
              {isMoviesTab && (
                <Checkbox
                  edge="start"
                  checked={selected.has(item.mediaFileId)}
                  onMouseDown={(e) => handleCheckboxMouseDown(e, idx, item.mediaFileId)}
                  onClick={(e) => e.stopPropagation()}
                  sx={{ mr: 1 }}
                />
              )}
              <ListItemText
                primary={item.title}
                secondary={
                  <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', mt: 0.3 }}>
                    {item.year && <Chip label={String(item.year)} size="small" variant="outlined" />}
                    {item.season && <Chip label={`S${item.season}`} size="small" variant="outlined" />}
                    {item.episode && <Chip label={`E${item.episode}`} size="small" variant="outlined" />}
                    {item.fileName && (
                      <Typography variant="caption" color="text.disabled" noWrap sx={{ maxWidth: 300 }}>
                        {item.fileName}
                      </Typography>
                    )}
                  </Box>
                }
              />
              <Box sx={{ display: 'flex', gap: 1 }}>
                {item.kind === 'show' && (
                  <Button
                    size="small"
                    variant="text"
                    onClick={(e) => { e.stopPropagation(); void keepWithoutTmdb(item.id) }}
                  >
                    Ponechat bez TMDB
                  </Button>
                )}
                <Button
                  size="small"
                  variant="outlined"
                  onClick={(e) => { e.stopPropagation(); openItem(item) }}
                >
                  Najít na TMDB
                </Button>
              </Box>
            </ListItemButton>
          ))}
        </List>
      )}

      <TmdbMatchDialog
        target={dialogTarget}
        onClose={() => setDialogTarget(null)}
        onSaved={afterSaved}
      />

      <BulkAssignDialog
        files={bulkFiles}
        open={bulkOpen}
        onClose={() => setBulkOpen(false)}
        onSaved={afterSaved}
      />
    </Box>
  )
}
