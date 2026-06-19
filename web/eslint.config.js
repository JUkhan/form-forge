import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    rules: {
      // Fast-Refresh hygiene hint, not a correctness rule — and inherently at odds with
      // TanStack Router's `export const Route` convention. Keep it visible as a warning
      // (with constant exports allowed) instead of failing CI.
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      // React Compiler rules from eslint-plugin-react-hooks v7. The codebase predates them;
      // surface as warnings so they don't block CI while they're cleaned up incrementally.
      'react-hooks/set-state-in-effect': 'warn',
      'react-hooks/refs': 'warn',
      // Allow intentionally-unused identifiers prefixed with `_` (e.g. mock callback params).
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrorsIgnorePattern: '^_' },
      ],
    },
  },
])
