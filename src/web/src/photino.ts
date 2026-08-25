// Photino injektuje window.external.sendMessage/receiveMessage jen uvnitř nativního okna
// (ve Vite dev serveru v běžném prohlížeči toto API neexistuje → fallback na HTML5 Fullscreen API).
export type PhotinoExternal = {
  sendMessage?: (message: string) => void
  receiveMessage?: (callback: (message: string) => void) => void
}

export function getPhotino(): PhotinoExternal | null {
  const ext = (window as unknown as { external?: PhotinoExternal }).external
  return ext?.sendMessage ? ext : null
}

/** Idempotentní zapnutí/vypnutí nativního fullscreenu (Photino). Mimo Photino no-op. */
export function setNativeFullscreen(on: boolean) {
  getPhotino()?.sendMessage?.(on ? 'fullscreen:on' : 'fullscreen:off')
}

export function toggleNativeFullscreen() {
  getPhotino()?.sendMessage?.('fullscreen:toggle')
}
