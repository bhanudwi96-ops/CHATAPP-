import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    open: true,
    proxy: {
      '/api': {
        target: 'https://localhost:7172',
        changeOrigin: true,
        secure: false,       // self-signed cert in dev
      },
      '/chatHub': {
        target: 'https://localhost:7172',
        changeOrigin: true,
        secure: false,
        ws: true,            // WebSocket proxy for SignalR
      },
    },
  },
});

