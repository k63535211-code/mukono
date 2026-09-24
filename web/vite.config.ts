import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, loadEnv } from 'vite';

const configDirectory = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig(({ mode }) => {
  // The repository-root .env is the single place for dev secrets (gitignored;
  // see .env.example). Shell variables still take precedence.
  const repositoryRoot = path.resolve(configDirectory, '..');
  const secrets = loadEnv(mode, repositoryRoot, '');
  const adminKey = process.env.ORGADMIN_API_KEY || secrets.ORGADMIN_API_KEY;

  const proxy = {
    target: 'http://localhost:5080',
    changeOrigin: true,
    ...(adminKey ? { headers: { 'X-Admin-Key': adminKey } } : {})
  };

  return {
    server: {
      host: '127.0.0.1',
      port: 5173,
      proxy: {
        '/api': proxy,
        '/health': proxy
      }
    }
  };
});
