// Tiny launcher: ensure cwd is the frontend project before booting Vite,
// so Tailwind/PostCSS resolve their configs and content globs correctly
// regardless of where the launcher invokes us from. Also point host-side
// previews at the published Docker API port so the page actually wires up.
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
process.chdir(here);

// Default the host preview at the docker-published API port.
if (!process.env.VITE_API_BASE_URL) {
  process.env.VITE_API_BASE_URL = 'http://localhost:38080';
}

await import('./node_modules/vite/bin/vite.js');
