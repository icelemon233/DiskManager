import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import product from './package.json' with { type: 'json' };
export default defineConfig({
  plugins: [react()],
  base: './',
  define: { __APP_VERSION__: JSON.stringify(product.version) },
  build: { sourcemap: true },
});
