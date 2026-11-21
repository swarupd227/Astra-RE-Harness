import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The API runs on :8000 (uvicorn); the dev server forwards /api to it.
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': 'http://localhost:8000' } },
});
