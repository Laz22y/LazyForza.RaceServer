import { cp, mkdir, readFile, readdir, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceDirectory = path.join(repositoryRoot, "src", "LazyForza.RaceServer.Web", "wwwroot");
const targetDirectory = path.join(repositoryRoot, "cloudflare", "public");
const checkOnly = process.argv.includes("--check");

if (path.dirname(targetDirectory) !== path.join(repositoryRoot, "cloudflare")) {
  throw new Error(`Refusing to replace Web assets outside the Cloudflare source tree: ${targetDirectory}`);
}

async function listFiles(root, current = root) {
  const entries = await readdir(current, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const fullPath = path.join(current, entry.name);
    if (entry.isDirectory()) files.push(...await listFiles(root, fullPath));
    else if (entry.isFile()) files.push(path.relative(root, fullPath).replaceAll(path.sep, "/"));
  }
  return files.sort();
}

async function assertSynchronized() {
  const [sourceFiles, targetFiles] = await Promise.all([
    listFiles(sourceDirectory),
    listFiles(targetDirectory).catch(() => [])
  ]);
  if (sourceFiles.join("\n") !== targetFiles.join("\n")) {
    throw new Error("Cloudflare Web assets are stale. Run: npm run generate:web");
  }
  for (const relativePath of sourceFiles) {
    const [source, target] = await Promise.all([
      readFile(path.join(sourceDirectory, relativePath)),
      readFile(path.join(targetDirectory, relativePath))
    ]);
    if (!source.equals(target)) {
      throw new Error(`Cloudflare Web asset differs: ${relativePath}`);
    }
  }
}

if (checkOnly) {
  await assertSynchronized();
} else {
  await rm(targetDirectory, { recursive: true, force: true });
  await mkdir(targetDirectory, { recursive: true });
  await cp(sourceDirectory, targetDirectory, { recursive: true, force: true });
  await assertSynchronized();
}
