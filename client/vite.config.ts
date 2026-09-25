/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The dev server proxies the API and the socket to the backend, so the browser only ever
// talks to one origin. That keeps CORS out of the picture entirely, and it mirrors how
// nginx will sit in front of both in the container build — one fewer difference between
// what runs on a laptop and what ships.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5138',
        changeOrigin: true,
      },
      '/ws': {
        target: 'ws://127.0.0.1:5138',
        ws: true,
      },
    },
  },
  test: {
    // jsdom rather than a real browser. The component tests here are about what the markup
    // says and what happens when it is clicked, not about layout or paint — and the parts
    // that genuinely need a browser were checked in one, through the DevTools protocol.
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      exclude: ['src/test/**', 'src/**/*.test.{ts,tsx}', 'src/main.tsx', 'src/vite-env.d.ts'],
    },
  },
})
