import { useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import { Alert, Backdrop } from '@mui/material'
import { LibraryProvider } from './library'
import { AppLayout } from './components/AppLayout'
import { Home } from './pages/Home'
import { Shows } from './pages/Shows'
import { Movies } from './pages/Movies'
import { ShowDetail } from './pages/ShowDetail'
import { MovieDetail } from './pages/MovieDetail'
import { Settings } from './pages/Settings'
import { WatchPage } from './pages/WatchPage'
import { ReviewQueue } from './pages/ReviewQueue'
import { api } from './api'
import { setNativeFullscreen, toggleNativeFullscreen } from './photino'
import './App.css'

const PING_INTERVAL_MS = 10_000
const FAILURES_BEFORE_OVERLAY = 3

/// Drží server naživu (viz ClientActivityTracker na serveru) a odhalí případ, kdy vlastník
/// mezitím skončil (job doběhl a nikdo se dlouho nehlásil) — zobrazí overlay místo tichého rozbití UI.
function useServerHeartbeat() {
  const [serverLost, setServerLost] = useState(false)

  useEffect(() => {
    let failures = 0
    const tick = async () => {
      const ok = await api.ping().catch(() => false)
      if (ok) {
        failures = 0
        setServerLost(false)
      } else if (++failures >= FAILURES_BEFORE_OVERLAY) {
        setServerLost(true)
      }
    }
    void tick()
    const timer = window.setInterval(tick, PING_INTERVAL_MS)
    return () => window.clearInterval(timer)
  }, [])

  return serverLost
}

function App() {
  const serverLost = useServerHeartbeat()

  // Globální pojistka: fullscreen jde vypnout i mimo Player (např. po zaseknutí)
  // — C# strana je idempotentní, kolize s Player ESC handlerem nevadí.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setNativeFullscreen(false)
        if (document.fullscreenElement) void document.exitFullscreen()
      } else if (e.key === 'F11') {
        e.preventDefault()
        toggleNativeFullscreen()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  return (
    <LibraryProvider>
      <BrowserRouter>
        <Routes>
          {/* Přehrávač – fullscreen, bez sidebaru. */}
          <Route path="/watch/:mediaFileId" element={<WatchPage />} />

          {/* Hlavní stránky s boční lištou. */}
          <Route element={<AppLayout />}>
            <Route path="/" element={<Home />} />
            <Route path="/shows" element={<Shows />} />
            <Route path="/movies" element={<Movies />} />
            <Route path="/show/:id" element={<ShowDetail />} />
            <Route path="/movie/:mediaFileId" element={<MovieDetail />} />
            <Route path="/review" element={<ReviewQueue />} />
            <Route path="/settings" element={<Settings />} />
          </Route>
        </Routes>
      </BrowserRouter>
      <Backdrop open={serverLost} sx={{ zIndex: (t) => t.zIndex.modal + 1 }}>
        <Alert severity="error" variant="filled" sx={{ maxWidth: 480 }}>
          Server byl ukončen — zavři okno a spusť aplikaci znovu.
        </Alert>
      </Backdrop>
    </LibraryProvider>
  )
}

export default App
