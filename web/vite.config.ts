import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Build to ../src/UsageTracker.Ingestion.Api/wwwroot so the API host serves the SPA
// straight from the published exe (Progressive Deployment ★: the .exe ships WITH its
// UI, no separate web server). Dev proxies /v1 + /health to the running API.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/UsageTracker.Ingestion.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/v1': 'http://127.0.0.1:5199',
      '/health': 'http://127.0.0.1:5199',
    },
  },
})
