import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { api, type Library } from './api'

type LibraryState = {
  library: Library | null
  error: string | null
  scanning: boolean
  reload: () => Promise<void>
  rescan: () => Promise<void>
}

const LibraryContext = createContext<LibraryState | null>(null)

export function LibraryProvider({ children }: { children: ReactNode }) {
  const [library, setLibrary] = useState<Library | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [scanning, setScanning] = useState(false)

  const reload = async () => {
    try {
      setLibrary(await api.getLibrary())
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }

  const rescan = async () => {
    setScanning(true)
    try {
      await api.scanAll()
      await reload()
    } catch (e) {
      setError(String(e))
    } finally {
      setScanning(false)
    }
  }

  useEffect(() => {
    void reload()
  }, [])

  return (
    <LibraryContext.Provider value={{ library, error, scanning, reload, rescan }}>
      {children}
    </LibraryContext.Provider>
  )
}

export function useLibrary(): LibraryState {
  const ctx = useContext(LibraryContext)
  if (!ctx) throw new Error('useLibrary must be used within LibraryProvider')
  return ctx
}
