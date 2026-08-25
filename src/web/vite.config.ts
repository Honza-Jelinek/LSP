import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    // Build se ukládá vedle hosta (LSP.App), odkud ho Kestrel servíruje.
    outDir: '../LSP.App/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // Dev režim: Vite dev server proxuje /api na Kestrel (pevný dev port).
    proxy: {
      '/api': 'http://localhost:5180',
    },
  },
})
