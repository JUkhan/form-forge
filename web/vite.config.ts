import { configDefaults, defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import { tanstackRouter } from '@tanstack/router-plugin/vite'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'

export default defineConfig({
  plugins: [
    tanstackRouter({
      target: 'react',
      autoCodeSplitting: true,
      routeFileIgnorePattern: '__tests__|\\.test\\.|\\.spec\\.',
    }),
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    strictPort: true,
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.VITE_API_BASE_URL ?? 'http://localhost:5190',
        changeOrigin: true,
        secure: false,
      },
    },
  },
  build: {
    target: 'es2022',
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
    // Extend vitest defaults so .git/.cache/.idea/coverage stay excluded.
    // Also exclude e2e/** so Vitest does not collect Playwright spec files
    // (Vitest's default include pattern matches **/*.spec.ts from project root).
    exclude: [...configDefaults.exclude, 'e2e/**'],
    // Windows + vitest 4 fork pool intermittently fails worker startup with
    // "Timeout waiting for worker to respond"; threads pool is reliable here.
    pool: 'threads',
  },
})
