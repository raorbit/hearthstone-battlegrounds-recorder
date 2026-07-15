import { fileURLToPath } from "node:url";
import preact from "@preact/preset-vite";
import { defineConfig } from "vite";

const webRoot = fileURLToPath(new URL(".", import.meta.url));
const outputDirectory = fileURLToPath(new URL("./dist", import.meta.url));

export default defineConfig(({ command }) => ({
  root: webRoot,
  base: "./",
  plugins: [
    preact(),
    {
      name: "bg-recorder-development-csp",
      // connect-src 'none' is intentional in production because this page owns a privileged native
      // bridge. Remove the meta only for Vite's local server so its HMR websocket can connect.
      transformIndexHtml(html) {
        return command === "serve"
          ? html.replace(/\s*<meta\s+http-equiv="Content-Security-Policy"[\s\S]*?\/>/i, "")
          : html;
      },
    },
  ],
  build: {
    outDir: outputDirectory,
    emptyOutDir: true,
    sourcemap: true,
  },
  server: {
    port: 5173,
    strictPort: true,
  },
}));
