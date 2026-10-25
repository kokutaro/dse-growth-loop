import { defineConfig } from "vite";
import { resolve } from "node:path";
export default defineConfig({
  server: {
    port: 5173,
    strictPort: true,
    proxy: { "/api": process.env.API_PROXY_TARGET ?? "http://127.0.0.1:5180" },
  },
  build: {
    rolldownOptions: {
      input: {
        main: resolve(import.meta.dirname, "index.html"),
        validation: resolve(import.meta.dirname, "validation.html"),
        skillDictionary: resolve(import.meta.dirname, "skill-dictionary.html"),
        redirect: resolve(import.meta.dirname, "redirect.html"),
      },
    },
  },
});
