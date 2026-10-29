import { defineConfig } from 'vite';

export default defineConfig({
  server: {
    proxy: { '/api': 'http://localhost:5050' },
  },
  build: { outDir: '../server/wwwroot', emptyOutDir: true },
});
