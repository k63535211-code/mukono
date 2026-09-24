import { defineConfig } from 'vite';

const adminKey = process.env.ORGADMIN_API_KEY;
const proxy = {
  target: 'http://localhost:5080',
  changeOrigin: true,
  ...(adminKey ? { headers: { 'X-Admin-Key': adminKey } } : {})
};

export default defineConfig({
  server: {
    host: '127.0.0.1',
    port: 5173,
    proxy: {
      '/api': proxy,
      '/health': proxy
    }
  }
});
