import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

/**
 * Admin API target for the dev proxy. Aspire injects service discovery variables; explicit
 * ADMINAPI_HTTP/ADMINAPI_HTTPS variables take precedence. Prefer discovered HTTPS for secure BFF cookies.
 */
function resolveAdminApiTarget(env: NodeJS.ProcessEnv): string {
  return (
    env.ADMINAPI_HTTPS ||
    env.ADMINAPI_HTTP ||
    env['services__adminapi__https__0'] ||
    env['services__adminapi__http__0'] ||
    'http://localhost:5180'
  )
}

const port = process.env.PORT ? Number(process.env.PORT) : 5173
const target = resolveAdminApiTarget(process.env)
const proxyOptions = { target, changeOrigin: false, xfwd: true, secure: false }

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  define: {
    __VUE_I18N_FULL_INSTALL__: true,
    __VUE_I18N_LEGACY_API__: false,
    __INTLIFY_PROD_DEVTOOLS__: false,
    __INTLIFY_DROP_MESSAGE_COMPILER__: false,
  },
  server: {
    host: 'localhost',
    port,
    strictPort: Boolean(process.env.PORT),
    proxy: {
      '/api': proxyOptions,
      '/bff': proxyOptions,
      '/signin-oidc': proxyOptions,
      '/signout-callback-oidc': proxyOptions,
    },
  },
  preview: {
    host: 'localhost',
    port,
    strictPort: Boolean(process.env.PORT),
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    chunkSizeWarningLimit: 900,
  },
  test: {
    environment: 'jsdom',
    globals: false,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.spec.ts'],
    css: false,
  },
})
