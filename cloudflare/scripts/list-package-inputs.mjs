import { readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
function files(directory) {
  return readdirSync(resolve(root, directory), { withFileTypes: true }).flatMap(entry => {
    const relative = `${directory}/${entry.name}`;
    if (entry.isSymbolicLink()) throw new Error(`Package inputs must not be symbolic links: ${relative}`);
    return entry.isDirectory() ? files(relative) : entry.isFile() ? [relative] : [];
  });
}
console.log(JSON.stringify([
  ...['public', 'src', 'tests', 'scripts'].flatMap(files),
  'package-lock.json', 'package.json', 'README.md', 'tsconfig.json', 'wrangler.jsonc'
].sort()));
