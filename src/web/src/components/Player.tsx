import { useCallback, useEffect, useRef, useState } from 'react'
import Hls from 'hls.js'
import { Box, Button, Chip, CircularProgress, Divider, IconButton, ListItemText, ListSubheader, Menu, MenuItem, Slider, Typography } from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import CheckIcon from '@mui/icons-material/Check'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import PauseIcon from '@mui/icons-material/Pause'
import FullscreenIcon from '@mui/icons-material/Fullscreen'
import FullscreenExitIcon from '@mui/icons-material/FullscreenExit'
import VolumeUpIcon from '@mui/icons-material/VolumeUp'
import VolumeOffIcon from '@mui/icons-material/VolumeOff'
import Replay5Icon from '@mui/icons-material/Replay5'
import Forward5Icon from '@mui/icons-material/Forward5'
import SkipPreviousIcon from '@mui/icons-material/SkipPrevious'
import SkipNextIcon from '@mui/icons-material/SkipNext'
import InsertCommentIcon from '@mui/icons-material/InsertComment'
import { api, type StreamInfo, type NextEpisode } from '../api'
import { getPhotino, setNativeFullscreen } from '../photino'

type PlayerProps = {
  mediaFileId: number
  title?: string
  /** Ignoruje uloženou pozici — vždy od začátku (z kontextového menu „Přehrát od začátku"). */
  fromStart?: boolean
  onClose: () => void
  /** Spustí další epizodu (autoplay / „Přehrát teď"). */
  onPlayNext?: (next: NextEpisode) => void
  onPlayPrevious?: (previous: NextEpisode) => void
}

// Kolik sekund před koncem se objeví „Další epizoda" popup a začne odpočet.
const AUTOPLAY_LEAD = 10

function formatTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '0:00'
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = Math.floor(seconds % 60)
  const mm = h > 0 ? String(m).padStart(2, '0') : String(m)
  return h > 0 ? `${h}:${mm}:${String(s).padStart(2, '0')}` : `${mm}:${String(s).padStart(2, '0')}`
}

export function Player({ mediaFileId, title, fromStart, onClose, onPlayNext, onPlayPrevious }: PlayerProps) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const containerRef = useRef<HTMLDivElement>(null)
  const hideTimer = useRef<number | undefined>(undefined)
  const resumeTo = useRef<number | null>(null)
  const playWhenReady = useRef(true)
  const playbackSession = useRef({ mediaFileId, id: crypto.randomUUID(), sequence: 0 })
  if (playbackSession.current.mediaFileId !== mediaFileId)
    playbackSession.current = { mediaFileId, id: crypto.randomUUID(), sequence: 0 }
  const progressReady = useRef(false)
  const metadataReady = useRef(false)
  const soundReady = useRef(false)
  const streamSession = useRef<string | null>(null)
  const streamMode = useRef<'direct' | 'hls' | null>(null)
  const soundSaveQueue = useRef<Promise<void>>(Promise.resolve())
  const cancelledAutoplay = useRef(false)
  // Inicializace na "teď" → první throttlované uložení proběhne až po ~5 s, ne hned na pozici 0.
  const lastSave = useRef<number>(Date.now())

  const [info, setInfo] = useState<StreamInfo | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [playing, setPlaying] = useState(false)
  const [buffering, setBuffering] = useState(true)
  const [current, setCurrent] = useState(0)
  const [duration, setDuration] = useState(0)
  const [volume, setVolume] = useState(1)
  const [muted, setMuted] = useState(false)
  const [progressRetry, setProgressRetry] = useState(0)
  const [loadRetry, setLoadRetry] = useState(0)
  const [endedCountdown, setEndedCountdown] = useState(false)
  const [controlsVisible, setControlsVisible] = useState(true)
  const [nextEpisode, setNextEpisode] = useState<NextEpisode | null>(null)
  const [previousEpisode, setPreviousEpisode] = useState<NextEpisode | null>(null)
  const [showNext, setShowNext] = useState(false)
  const [countdown, setCountdown] = useState(10)
  const [isFullscreen, setIsFullscreen] = useState(false)
  const [selectedAudioOrdinal, setSelectedAudioOrdinal] = useState<number | null>(null)
  const [selectedSubtitleId, setSelectedSubtitleId] = useState<string | null>(null)
  const [trackMenuAnchor, setTrackMenuAnchor] = useState<HTMLElement | null>(null)

  useEffect(() => {
    setSelectedAudioOrdinal(null)
    setSelectedSubtitleId(null)
    resumeTo.current = null
    playWhenReady.current = true
    cancelledAutoplay.current = false
    progressReady.current = false
    metadataReady.current = false
  }, [mediaFileId])

  const startIfReady = useCallback(() => {
    const video = videoRef.current
    if (!video || !progressReady.current || !metadataReady.current || !soundReady.current) return
    if (resumeTo.current != null) {
      const target = resumeTo.current
      if (Number.isFinite(video.duration) && target < video.duration - 10)
        video.currentTime = Math.max(0, target)
      resumeTo.current = null
    }
    if (playWhenReady.current) void video.play().catch(() => setPlaying(false))
    else { video.pause(); setPlaying(false) }
  }, [])

  useEffect(() => {
    let cancelled = false
    soundReady.current = false
    progressReady.current = false
    setError(null)
    const session = playbackSession.current.id
    void api.getProgress(mediaFileId, session).then((p) => {
      if (cancelled || playbackSession.current.id !== session) return
      if (!fromStart && p && !p.finished && p.positionSeconds > 5)
        resumeTo.current = p.positionSeconds
      progressReady.current = true
      startIfReady()
    }).catch((e: unknown) => {
      if (!cancelled) setError(`Uloženou pozici nelze načíst: ${String(e)}`)
    })
    return () => { cancelled = true }
  }, [mediaFileId, fromStart, progressRetry, startIfReady])

  useEffect(() => {
    let cancelled = false
    void api.getPlayerSound().then((sound) => {
      if (cancelled) return
      const video = videoRef.current
      if (video) { video.volume = sound.volume; video.muted = sound.muted }
      setVolume(sound.volume)
      setMuted(sound.muted)
      soundReady.current = true
      startIfReady()
    }).catch((e: unknown) => { if (!cancelled) setError(`Nastavení zvuku nelze načíst: ${String(e)}`) })
    return () => { cancelled = true }
  }, [startIfReady, loadRetry])

  // Načti info o další epizodě (pro autoplay).
  useEffect(() => {
    setNextEpisode(null)
    setPreviousEpisode(null)
    setShowNext(false)
    let cancelled = false
    void api.getNextEpisode(mediaFileId).then((next) => { if (!cancelled) setNextEpisode(next) }).catch(() => undefined)
    void api.getPreviousEpisode(mediaFileId).then((previous) => { if (!cancelled) setPreviousEpisode(previous) }).catch(() => undefined)
    return () => { cancelled = true }
  }, [mediaFileId])

  // Načti info o streamu a napoj zdroj (direct = src, hls = hls.js).
  useEffect(() => {
    let hls: Hls | null = null
    let cancelled = false
    const session = crypto.randomUUID()
    streamSession.current = session
    streamMode.current = null
    const videoAtStart = videoRef.current
    metadataReady.current = false
    setBuffering(true)
    const release = () => { void api.purgeSegments(mediaFileId, session).catch(() => undefined) }
    const heartbeat = () => {
      const video = videoAtStart
      if (!cancelled && video && streamMode.current === 'hls')
        void api.heartbeatStream(mediaFileId, session, video.paused || video.readyState < 3,
          Number.isFinite(video.currentTime) ? video.currentTime : 0).catch(() => undefined)
    }
    const heartbeatTimer = window.setInterval(heartbeat, 10000)
    window.addEventListener('pagehide', release)

    api
      .getStreamInfo(mediaFileId, selectedAudioOrdinal, session)
      .then((streamInfo) => {
        if (cancelled) { release(); return }
        streamMode.current = streamInfo.mode
        setInfo(streamInfo)
        const video = videoRef.current
        if (!video) return

        if (streamInfo.mode === 'hls' && Hls.isSupported()) {
          hls = new Hls({
            enableWorker: true,
            // Segment se na serveru může (re)generovat (seek/transkód) – dej mu čas, nehlas timeout hned.
            fragLoadingTimeOut: 60000,
            fragLoadingMaxRetry: 8,
            maxBufferLength: 30,
          })
          hls.loadSource(streamInfo.url)
          hls.attachMedia(video)
          hls.on(Hls.Events.ERROR, (_e, data) => {
            if (data.fatal) setError(`Chyba přehrávání: ${data.type}`)
          })
        } else {
          // Direct play, nebo nativní HLS (Safari).
          video.src = streamInfo.url
        }
        heartbeat()
      })
      .catch((e: unknown) => { if (!cancelled) setError(String(e)) })

    return () => {
      cancelled = true
      window.clearInterval(heartbeatTimer)
      window.removeEventListener('pagehide', release)
      hls?.destroy()
      if (videoAtStart) { videoAtStart.pause(); videoAtStart.removeAttribute('src'); videoAtStart.load() }
      release()
      if (streamSession.current === session) streamSession.current = null
      streamMode.current = null
    }
  }, [mediaFileId, selectedAudioOrdinal, loadRetry])

  const showControlsTemporarily = useCallback(() => {
    setControlsVisible(true)
    window.clearTimeout(hideTimer.current)
    hideTimer.current = window.setTimeout(() => {
      if (videoRef.current && !videoRef.current.paused) setControlsVisible(false)
    }, 2800)
  }, [])

  const togglePlay = useCallback(() => {
    const video = videoRef.current
    if (!video || !progressReady.current || !soundReady.current || !metadataReady.current) return
    if (video.paused) void video.play()
    else video.pause()
  }, [])

  const seekBy = useCallback((seconds: number) => {
    const video = videoRef.current
    if (!video || !progressReady.current || !metadataReady.current) return
    const durationLimit = Number.isFinite(video.duration) ? video.duration : Number.POSITIVE_INFINITY
    video.currentTime = Math.max(0, Math.min(durationLimit, video.currentTime + seconds))
    showControlsTemporarily()
  }, [showControlsTemporarily])

  const selectAudioTrack = useCallback((ordinal: number) => {
    if (ordinal === (info?.selectedAudioOrdinal ?? selectedAudioOrdinal)) {
      setTrackMenuAnchor(null)
      return
    }
    const track = info?.audioTracks.find((t) => t.ordinal === ordinal)
    const video = videoRef.current
    if (video) {
      resumeTo.current = video.currentTime
      playWhenReady.current = !video.paused
      setCurrent(video.currentTime)
    }
    setTrackMenuAnchor(null)
    setSelectedAudioOrdinal(ordinal)
    if (track?.normalizedLanguage)
      void api.savePlayerAudioLanguage(track.normalizedLanguage).catch(() => undefined)
  }, [info, selectedAudioOrdinal])

  const selectSubtitleTrack = useCallback((id: string | null) => {
    setSelectedSubtitleId(id)
    setTrackMenuAnchor(null)
  }, [])

  const savePosition = useCallback((video: HTMLVideoElement | null) => {
    if (!video || !progressReady.current || resumeTo.current != null || video.currentTime < 3) return
    const session = playbackSession.current
    if (session.mediaFileId !== mediaFileId) return
    const sequence = ++session.sequence
    void api.saveProgress(mediaFileId, video.currentTime,
      Number.isFinite(video.duration) ? video.duration : null, session.id, sequence)
      .catch(() => undefined)
  }, [mediaFileId])

  const saveNow = useCallback(() => savePosition(videoRef.current), [savePosition])

  // Ulož pozici při zavření přehrávače.
  useEffect(() => {
    const video = videoRef.current
    return () => savePosition(video)
  }, [savePosition])

  // Při odmountování vždy vypni fullscreen (idempotentní) — jinak okno zůstane
  // zaseknuté ve fullscreenu bez klávesových zkratek, které žily jen tady.
  useEffect(() => () => {
    setNativeFullscreen(false)
    if (document.fullscreenElement) void document.exitFullscreen()
  }, [])

  const playNext = useCallback(() => {
    if (!nextEpisode) return
    saveNow()
    onPlayNext?.(nextEpisode)
  }, [nextEpisode, onPlayNext, saveNow])

  const playPrevious = useCallback(() => {
    if (!previousEpisode) return
    saveNow()
    onPlayPrevious?.(previousEpisode)
  }, [previousEpisode, onPlayPrevious, saveNow])

  const handleEnded = useCallback(() => {
    saveNow()
    if (!nextEpisode || cancelledAutoplay.current) return
    if (showNext) { playNext(); return }
    // Některé zdroje přeskočí události času blízko konce; pak dáme uživateli odpočet.
    if (nextEpisode) {
      setCountdown(AUTOPLAY_LEAD)
      setShowNext(true)
      setEndedCountdown(true)
    }
  }, [nextEpisode, saveNow, showNext, playNext])

  // Odpočet do automatického přehrání další epizody.
  useEffect(() => {
    if (!showNext || !endedCountdown || cancelledAutoplay.current) return
    if (countdown <= 0) {
      playNext()
      return
    }
    const t = window.setTimeout(() => setCountdown((c) => c - 1), 1000)
    return () => window.clearTimeout(t)
  }, [showNext, endedCountdown, countdown, playNext])

  const toggleFullscreen = useCallback(() => {
    const photino = getPhotino()
    if (photino?.sendMessage) {
      photino.sendMessage('fullscreen:toggle')
      return
    }
    const el = containerRef.current
    if (!el) return
    if (document.fullscreenElement) void document.exitFullscreen()
    else void el.requestFullscreen()
  }, [])

  // Stav fullscreenu se vrací zprávou z nativního okna (Photino), nebo (mimo Photino) z browser API.
  useEffect(() => {
    const photino = getPhotino()
    if (photino?.receiveMessage) {
      photino.receiveMessage((message) => {
        if (message === 'fullscreen:on') setIsFullscreen(true)
        else if (message === 'fullscreen:off') setIsFullscreen(false)
      })
      return
    }
    const onChange = () => setIsFullscreen(!!document.fullscreenElement)
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [])

  // Klávesové zkratky (mezerník, šipky, F, Esc).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      switch (e.key) {
        case ' ': e.preventDefault(); togglePlay(); break
        case 'ArrowRight': seekBy(10); break
        case 'ArrowLeft': seekBy(-10); break
        case 'f': toggleFullscreen(); break
        case 'Escape': if (isFullscreen) toggleFullscreen(); else onClose(); break
      }
      showControlsTemporarily()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [togglePlay, seekBy, toggleFullscreen, onClose, showControlsTemporarily, isFullscreen])

  const activeAudioOrdinal = info?.selectedAudioOrdinal ?? selectedAudioOrdinal
  const audioTracks = info?.audioTracks ?? []
  const subtitleTracks = info?.subtitleTracks ?? []
  const selectedSubtitle = subtitleTracks.find((track) => track.id === selectedSubtitleId && track.isPlayable && track.url)
  const hasTrackMenu = audioTracks.length > 0 || subtitleTracks.length > 0
  const hasEpisodeNavigation = previousEpisode != null || nextEpisode != null

  useEffect(() => {
    const video = videoRef.current
    if (!video) return

    const apply = () => {
      for (let i = 0; i < video.textTracks.length; i++) {
        const track = video.textTracks[i]
        track.mode = selectedSubtitle && track.label === selectedSubtitle.label ? 'showing' : 'disabled'
      }
    }

    apply()
    const timer = window.setTimeout(apply, 100)
    return () => window.clearTimeout(timer)
  }, [selectedSubtitle])

  return (
    <Box
      ref={containerRef}
      onMouseMove={showControlsTemporarily}
      sx={{
        position: 'fixed', inset: 0, bgcolor: '#000', zIndex: 100, overflow: 'hidden',
        cursor: controlsVisible ? 'default' : 'none',
      }}
    >
      <video
        ref={videoRef}
        onClick={togglePlay}
        onPlay={() => { setPlaying(true); showControlsTemporarily() }}
        onPause={(e) => {
          setPlaying(false); setControlsVisible(true); savePosition(e.currentTarget)
          if (streamSession.current && streamMode.current === 'hls')
            void api.heartbeatStream(mediaFileId, streamSession.current, true, e.currentTarget.currentTime).catch(() => undefined)
        }}
        onWaiting={() => setBuffering(true)}
        onPlaying={(e) => {
          setBuffering(false)
          if (streamSession.current && streamMode.current === 'hls')
            void api.heartbeatStream(mediaFileId, streamSession.current, false, e.currentTarget.currentTime).catch(() => undefined)
        }}
        onLoadedMetadata={(e) => {
          const v = e.currentTarget
          setDuration(v.duration)
          metadataReady.current = true
          startIfReady()
        }}
        onTimeUpdate={(e) => {
          const v = e.currentTarget
          setCurrent(v.currentTime)
          const now = Date.now()
          if (progressReady.current && resumeTo.current == null && v.currentTime >= 3 && now - lastSave.current > 5000) {
            lastSave.current = now
            savePosition(v)
          }
          if (!endedCountdown && nextEpisode && !cancelledAutoplay.current && Number.isFinite(v.duration)) {
            const remaining = v.duration - v.currentTime
            if (remaining > AUTOPLAY_LEAD || remaining < 0 || v.currentTime <= 0) setShowNext(false)
            else {
              setCountdown(Math.max(1, Math.ceil(remaining)))
              setShowNext(true)
            }
          }
        }}
        onSeeking={(e) => {
          if (Number.isFinite(e.currentTarget.duration) &&
              e.currentTarget.duration - e.currentTarget.currentTime > AUTOPLAY_LEAD) {
            setShowNext(false)
            setEndedCountdown(false)
          }
        }}
        onDurationChange={(e) => setDuration(e.currentTarget.duration)}
        onVolumeChange={(e) => {
          const v = e.currentTarget
          setVolume(v.volume); setMuted(v.muted)
          if (soundReady.current) {
            const nextVolume = v.volume, nextMuted = v.muted
            soundSaveQueue.current = soundSaveQueue.current
              .then(async () => { await api.savePlayerSound(nextVolume, nextMuted) }).catch(() => undefined)
          }
        }}
        onEnded={handleEnded}
        style={{ width: '100%', height: '100%', objectFit: 'contain', background: '#000' }}
      >
        {selectedSubtitle?.url && (
          <track
            key={selectedSubtitle.id}
            kind="subtitles"
            src={selectedSubtitle.url}
            srcLang={selectedSubtitle.normalizedLanguage ?? undefined}
            label={selectedSubtitle.label}
            default
          />
        )}
      </video>

      {/* Buffering spinner */}
      {buffering && !error && (
        <Box sx={{ position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%,-50%)' }}>
          <CircularProgress sx={{ color: 'primary.main' }} />
        </Box>
      )}

      {/* Error */}
      {error && (
        <Box sx={{ position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 2 }}>
          <Typography color="error">{error}</Typography>
          <Button variant="outlined" onClick={() => { setProgressRetry((n) => n + 1); setLoadRetry((n) => n + 1); setError(null) }}>Zkusit znovu</Button>
          <Button variant="outlined" onClick={onClose}>Zpět</Button>
        </Box>
      )}

      {/* Autoplay overlay */}
      {showNext && nextEpisode && (
        <Box sx={{ position: 'absolute', right: 32, bottom: 96, zIndex: 5 }}>
          <Box sx={{ bgcolor: 'rgba(20,20,26,0.92)', border: '1px solid rgba(255,255,255,0.12)', borderRadius: 2, p: 2, maxWidth: 360, boxShadow: 8 }}>
            <Typography variant="caption" sx={{ textTransform: 'uppercase', color: 'text.secondary', letterSpacing: '0.04em' }}>
              Další epizoda za {countdown} s
            </Typography>
            <Typography sx={{ fontWeight: 600, mt: 0.5 }}>
              S{String(nextEpisode.season).padStart(2, '0')}E{String(nextEpisode.number).padStart(2, '0')}
              {nextEpisode.title ? ` · ${nextEpisode.title}` : ''}
            </Typography>
            <Box sx={{ display: 'flex', gap: 1, mt: 1.5 }}>
              <Button variant="contained" startIcon={<SkipNextIcon />} onClick={playNext} sx={{ flex: 1 }}>
                Přehrát teď
              </Button>
              <Button variant="outlined" onClick={() => { cancelledAutoplay.current = true; setShowNext(false); setEndedCountdown(false) }}>Zrušit</Button>
            </Box>
          </Box>
        </Box>
      )}

      {/* OVERLAY – top bar + controls */}
      <Box sx={{
        position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column', justifyContent: 'space-between',
        opacity: controlsVisible ? 1 : 0, pointerEvents: controlsVisible ? 'auto' : 'none',
        transition: 'opacity 0.25s ease',
      }}>
        {/* Top bar */}
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, p: 1.5, background: 'linear-gradient(to bottom, rgba(0,0,0,0.7), transparent)' }}>
          <IconButton onClick={onClose} size="small" sx={{ color: '#fff' }}>
            <ArrowBackIcon />
          </IconButton>
          <Typography sx={{ fontWeight: 600, textShadow: '0 1px 4px rgba(0,0,0,0.8)' }}>
            {title ?? 'Přehrávání'}
          </Typography>
          {info && <Chip label={info.mode === 'hls' ? 'HLS' : 'DIRECT'} size="small" sx={{ ml: 1 }} />}
        </Box>

        {/* Big play button */}
        {!playing && !buffering && !error && (
          <IconButton
            onClick={togglePlay}
            sx={{
              alignSelf: 'center', m: 'auto', width: 80, height: 80,
              bgcolor: 'rgba(0,0,0,0.5)', color: '#fff',
              '&:hover': { bgcolor: 'primary.main' },
            }}
          >
            <PlayArrowIcon sx={{ fontSize: 48 }} />
          </IconButton>
        )}

        {/* Bottom controls */}
        <Box sx={{ px: 2, pb: 1.5, background: 'linear-gradient(to top, rgba(0,0,0,0.8), transparent)' }}>
          {/* Seek slider */}
          <Slider
            value={current}
            max={duration || 1}
            onChange={(_e, v) => {
              const video = videoRef.current
              if (video && progressReady.current && metadataReady.current) video.currentTime = v as number
            }}
            sx={{
              color: 'primary.main', height: 4, p: '4px 0',
              '& .MuiSlider-thumb': { width: 14, height: 14, transition: 'none' },
              '& .MuiSlider-rail': { bgcolor: 'rgba(255,255,255,0.25)' },
            }}
          />
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            {hasEpisodeNavigation && (
              <IconButton
                onClick={playPrevious}
                disabled={!previousEpisode}
                size="small"
                sx={{ color: '#fff', '&.Mui-disabled': { color: 'rgba(255,255,255,0.25)' } }}
              >
                <SkipPreviousIcon />
              </IconButton>
            )}
            <IconButton onClick={() => seekBy(-5)} size="small" sx={{ color: '#fff' }}>
              <Replay5Icon />
            </IconButton>
            <IconButton onClick={togglePlay} size="small" sx={{ color: '#fff' }}>
              {playing ? <PauseIcon /> : <PlayArrowIcon />}
            </IconButton>
            <IconButton onClick={() => seekBy(5)} size="small" sx={{ color: '#fff' }}>
              <Forward5Icon />
            </IconButton>
            {hasEpisodeNavigation && (
              <IconButton
                onClick={playNext}
                disabled={!nextEpisode}
                size="small"
                sx={{ color: '#fff', '&.Mui-disabled': { color: 'rgba(255,255,255,0.25)' } }}
              >
                <SkipNextIcon />
              </IconButton>
            )}
            <Typography variant="caption" sx={{ color: 'rgba(255,255,255,0.85)', fontVariantNumeric: 'tabular-nums', minWidth: 100 }}>
              {formatTime(current)} / {formatTime(duration)}
            </Typography>
            <Box sx={{ flex: 1 }} />
            <IconButton onClick={() => { if (videoRef.current) videoRef.current.muted = !videoRef.current.muted }} size="small" sx={{ color: '#fff' }}>
              {muted ? <VolumeOffIcon /> : <VolumeUpIcon />}
            </IconButton>
            <Slider
              value={volume}
              min={0} max={1} step={0.05}
              onChange={(_e, v) => { if (videoRef.current) videoRef.current.volume = v as number }}
              sx={{ width: 90, color: 'primary.main', '& .MuiSlider-thumb': { width: 12, height: 12 } }}
            />
            <IconButton
              onClick={(e) => setTrackMenuAnchor(e.currentTarget)}
              disabled={!hasTrackMenu}
              size="small"
              sx={{
                color: selectedSubtitleId ? 'primary.main' : '#fff',
                '&.Mui-disabled': { color: 'rgba(255,255,255,0.25)' },
              }}
            >
              <InsertCommentIcon />
            </IconButton>
            <Menu
              anchorEl={trackMenuAnchor}
              open={Boolean(trackMenuAnchor)}
              onClose={() => setTrackMenuAnchor(null)}
              slotProps={{ paper: { sx: { minWidth: 300 } } }}
            >
              {audioTracks.length > 0 && <ListSubheader disableSticky>Dabing</ListSubheader>}
              {audioTracks.map((track) => (
                <MenuItem
                  key={track.ordinal}
                  selected={track.ordinal === activeAudioOrdinal}
                  onClick={() => selectAudioTrack(track.ordinal)}
                >
                  <CheckIcon
                    fontSize="small"
                    sx={{
                      mr: 1,
                      opacity: track.ordinal === activeAudioOrdinal ? 1 : 0,
                    }}
                  />
                  <ListItemText
                    primary={track.label}
                    secondary={[
                      track.normalizedLanguage?.toUpperCase(),
                      track.isDefault ? 'default' : null,
                    ].filter(Boolean).join(' · ')}
                  />
                </MenuItem>
              ))}
              {audioTracks.length > 0 && subtitleTracks.length > 0 && <Divider />}
              {subtitleTracks.length > 0 && <ListSubheader disableSticky>Titulky</ListSubheader>}
              {subtitleTracks.length > 0 && (
                <MenuItem selected={selectedSubtitleId == null} onClick={() => selectSubtitleTrack(null)}>
                  <CheckIcon fontSize="small" sx={{ mr: 1, opacity: selectedSubtitleId == null ? 1 : 0 }} />
                  <ListItemText primary="Vypnuto" />
                </MenuItem>
              )}
              {subtitleTracks.map((track) => (
                <MenuItem
                  key={track.id}
                  selected={track.id === selectedSubtitleId}
                  disabled={!track.isPlayable || !track.url}
                  onClick={() => selectSubtitleTrack(track.id)}
                >
                  <CheckIcon
                    fontSize="small"
                    sx={{
                      mr: 1,
                      opacity: track.id === selectedSubtitleId ? 1 : 0,
                    }}
                  />
                  <ListItemText
                    primary={track.label}
                    secondary={[
                      track.normalizedLanguage?.toUpperCase(),
                      track.source === 'sidecar' ? 'soubor' : 'interní',
                      track.isForced ? 'forced' : null,
                      !track.isPlayable ? 'nepodporované' : null,
                    ].filter(Boolean).join(' · ')}
                  />
                </MenuItem>
              ))}
            </Menu>
            <IconButton onClick={toggleFullscreen} size="small" sx={{ color: '#fff' }}>
              {isFullscreen ? <FullscreenExitIcon /> : <FullscreenIcon />}
            </IconButton>
          </Box>
        </Box>
      </Box>
    </Box>
  )
}
