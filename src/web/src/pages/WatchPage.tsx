import { useLocation, useNavigate, useParams } from 'react-router-dom'
import { Player } from '../components/Player'
import type { NextEpisode } from '../api'

export function WatchPage() {
  const { mediaFileId } = useParams()
  const navigate = useNavigate()
  const location = useLocation()
  const state = location.state as { title?: string; fromStart?: boolean } | null
  const title = state?.title
  const fromStart = state?.fromStart

  const id = Number(mediaFileId)
  if (!Number.isFinite(id)) {
    navigate('/')
    return null
  }

  const playEpisode = (episode: NextEpisode) => {
    const label = `S${String(episode.season).padStart(2, '0')}E${String(episode.number).padStart(2, '0')}`
    navigate(`/watch/${episode.mediaFileId}`, {
      replace: true,
      state: { title: `${episode.showTitle} - ${label}` },
    })
  }

  return (
    <Player
      key={id}
      mediaFileId={id}
      title={title}
      fromStart={fromStart}
      onClose={() => navigate(-1)}
      onPlayNext={playEpisode}
      onPlayPrevious={playEpisode}
    />
  )
}
