// ESLint flat config for the web client.
//
// Application TypeScript (src/ and e2e/) is linted with type information so that
// an `any` fails `npm run lint` whether it is written explicitly or arrives from
// a library or an inferred expression such as JSON.parse. The generated
// src/api/schema.ts is ignored. The TypeScript tooling configs, vite.config.ts and
// playwright.config.ts, stay outside that type-aware scope: they are linted without
// type information, and `tsc --noEmit` type-checks them.
import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import tseslint from 'typescript-eslint';

export default defineConfig([
  globalIgnores([
    'dist/**',
    'src/api/schema.ts',
    'test-results/**',
    'playwright-report/**',
    'coverage/**',
    'node_modules/**',
  ]),

  // This file and the Node tools in scripts/*.mjs: plain JavaScript, no type information.
  {
    files: ['**/*.{js,mjs}'],
    extends: [js.configs.recommended],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: {
        process: 'readonly',
        console: 'readonly',
        URL: 'readonly',
        Buffer: 'readonly',
      },
    },
  },

  // The TypeScript tooling configs: typescript-eslint without type information.
  {
    files: ['vite.config.ts', 'playwright.config.ts'],
    extends: [js.configs.recommended, tseslint.configs.recommended],
    rules: {
      // TypeScript resolves identifiers itself, including Node globals such as process.
      'no-undef': 'off',
    },
  },

  {
    files: ['src/**/*.{ts,tsx}', 'e2e/**/*.ts'],
    extends: [js.configs.recommended, tseslint.configs.recommendedTypeChecked],
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      // Zero `any`: stated explicitly although recommendedTypeChecked enables them.
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/no-unsafe-assignment': 'error',
      '@typescript-eslint/no-unsafe-member-access': 'error',
      '@typescript-eslint/no-unsafe-call': 'error',
      '@typescript-eslint/no-unsafe-return': 'error',
      '@typescript-eslint/no-unsafe-argument': 'error',

      '@typescript-eslint/no-floating-promises': 'error',
      // Async event handlers such as onClick={async () => …} are allowed in JSX attributes.
      '@typescript-eslint/no-misused-promises': ['error', { checksVoidReturn: { attributes: false } }],

      // TypeScript resolves identifiers itself, including DOM and JSX globals.
      'no-undef': 'off',
    },
  },
]);
