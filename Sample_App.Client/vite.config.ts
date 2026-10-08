import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig(({ command }) => {
  // Defaults to the VS launch profile port; override to point at a docker-compose
  // instance instead, e.g. VITE_DEV_PROXY_TARGET=http://localhost:18082 npm run dev.
  const proxyTarget = process.env.VITE_DEV_PROXY_TARGET ?? 'https://localhost:7007'

  return {
    // Production is served behind an IIS reverse proxy under /chat/ (guto.ca/chat);
    // local dev (via Vite's own server, or VS's multi-startup) stays at root.
    base: command === 'build' ? '/chat/' : '/',
    // Without this, esbuild's CSS minifier rewrites e.g. "@media (max-width: 768px)" to the
    // newer range syntax "(width <= 768px)", which pre-16.4 Safari silently ignores --
    // breaking the mobile layout instead of just missing an optimization.
    build: {
      cssTarget: 'safari14',
    },
    plugins: [react()],
    server: {
      port: 39294,
      proxy: {
        '/chatHub': {
          target: proxyTarget,
          ws: true,
          secure: false,
        },
        '/api': {
          target: proxyTarget,
          secure: false,
        },
      },
    },
  }
})
