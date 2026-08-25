import { useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, Divider, Menu, MenuItem,
} from '@mui/material'
import EditIcon from '@mui/icons-material/Edit'
import RestartAltIcon from '@mui/icons-material/RestartAlt'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlined'
import SaveAltIcon from '@mui/icons-material/SaveAlt'
import { api } from '../api'
import { useLibrary } from '../library'
import { portableExportPath } from '../portableExport'
import { TmdbMatchDialog, type MatchTarget } from './TmdbMatchDialog'
import { BulkAssignDialog, type AssignFile } from './BulkAssignDialog'

/**
 * Sdílené kontextové menu (pravý klik) pro média. Soubory otevírají BulkAssignDialog
 * (stejné UI jako Ke kontrole), seriály TmdbMatchDialog.
 * Použití: const m = useTmdbMatch(); na kartu dej onContextMenu={(e) => m.open(e, target)}; a vykresli {m.element}.
 */
export function useTmdbMatch() {
  const { reload } = useLibrary()
  const navigate = useNavigate()
  const [menu, setMenu] = useState<{ x: number; y: number; target: MatchTarget } | null>(null)
  const [dialogTarget, setDialogTarget] = useState<MatchTarget | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<MatchTarget | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [bulkFiles, setBulkFiles] = useState<AssignFile[] | null>(null)
  const [bulkType, setBulkType] = useState<'movie' | 'tv'>('movie')

  const open = (e: React.MouseEvent, target: MatchTarget) => {
    e.preventDefault()
    setMenu({ x: e.clientX, y: e.clientY, target })
  }

  const openEdit = (t: MatchTarget) => {
    if (t.kind === 'file') {
      setBulkFiles([{
        mediaFileId: t.mediaFileId, title: t.title, fileName: '',
        season: t.season ?? null, episode: t.episode ?? null,
      }])
      setBulkType(t.isEpisode ? 'tv' : 'movie')
    } else {
      setDialogTarget(t)
    }
  }

  const canExport = menu?.target.kind === 'show'
    || (menu?.target.kind === 'file' && !menu.target.isEpisode)

  const openExport = (target: MatchTarget) => {
    if (target.kind === 'show') {
      navigate(portableExportPath({ kind: 'show', showId: target.showId }))
    } else if (!target.isEpisode) {
      navigate(portableExportPath({ kind: 'movie', mediaFileId: target.mediaFileId }))
    }
  }

  const element: ReactNode = (
    <>
      <Menu
        open={menu !== null}
        onClose={() => setMenu(null)}
        anchorReference="anchorPosition"
        anchorPosition={menu ? { top: menu.y, left: menu.x } : undefined}
      >
        {canExport && (
          <MenuItem
            onClick={() => {
              openExport(menu!.target)
              setMenu(null)
            }}
          >
            <SaveAltIcon fontSize="small" sx={{ mr: 1 }} />
            Exportovat na disk…
          </MenuItem>
        )}
        {canExport && <Divider />}
        <MenuItem
          onClick={() => {
            openEdit(menu!.target)
            setMenu(null)
          }}
        >
          <EditIcon fontSize="small" sx={{ mr: 1 }} />
          Najít na TMDB / Upravit
        </MenuItem>
        <MenuItem
          onClick={async () => {
            const t = menu!.target
            setMenu(null)
            if (t.kind === 'show') await api.resetMatchShow(t.showId)
            else await api.resetMatchFile(t.mediaFileId)
            await reload()
          }}
        >
          <RestartAltIcon fontSize="small" sx={{ mr: 1 }} />
          Resetovat na automatické
        </MenuItem>
        <Divider />
        <MenuItem
          sx={{ color: 'error.main' }}
          onClick={() => {
            setDeleteTarget(menu!.target)
            setMenu(null)
          }}
        >
          <DeleteOutlineIcon fontSize="small" sx={{ mr: 1 }} />
          Odstranit záznam…
        </MenuItem>
      </Menu>
      <Dialog open={deleteTarget !== null} onClose={() => { if (!deleting) setDeleteTarget(null) }}>
        <DialogTitle>Odstranit záznam?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Soubory zůstanou na disku. LSP smaže přiřazení, epizody a ruční opravy tohoto záznamu
            a vrátí soubory do Ke kontrole.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setDeleteTarget(null)} disabled={deleting}>Zrušit</Button>
          <Button
            variant="contained" color="error" disabled={deleting}
            onClick={async () => {
              const t = deleteTarget!
              setDeleting(true)
              try {
                if (t.kind === 'show') await api.deleteRecordShow(t.showId)
                else await api.deleteRecordFile(t.mediaFileId)
                await reload()
              } finally {
                setDeleting(false)
                setDeleteTarget(null)
              }
            }}
          >
            Odstranit záznam
          </Button>
        </DialogActions>
      </Dialog>
      <TmdbMatchDialog
        target={dialogTarget}
        onClose={() => setDialogTarget(null)}
        onSaved={() => void reload()}
      />
      <BulkAssignDialog
        files={bulkFiles ?? []}
        open={bulkFiles !== null}
        initialType={bulkType}
        onClose={() => setBulkFiles(null)}
        onSaved={() => void reload()}
      />
    </>
  )

  return { open, element }
}
