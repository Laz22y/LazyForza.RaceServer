import { access } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const cloudflareRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repositoryRoot = path.resolve(cloudflareRoot, "..");
const protocolGenerator = path.join(repositoryRoot, "scripts", "generate-protocol.mjs");
const webGenerator = path.join(repositoryRoot, "scripts", "generate-web-assets.mjs");
const checkOnly = process.argv.includes("--check");

if (await exists(protocolGenerator) && await exists(webGenerator)) {
  run(protocolGenerator, [checkOnly ? "--check" : null, "--skip-client"]);
  run(webGenerator, [checkOnly ? "--check" : null]);
} else {
  await Promise.all([
    access(path.join(cloudflareRoot, "src", "protocol.generated.ts")),
    access(path.join(cloudflareRoot, "public", "index.html"))
  ]);
  console.log("Using committed generated assets from the standalone Cloudflare package.");
}

function run(script, argumentsList) {
  const result = spawnSync(process.execPath, [script, ...argumentsList.filter(Boolean)], {
    cwd: repositoryRoot,
    stdio: "inherit"
  });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}

async function exists(targetPath) {
  try {
    await access(targetPath);
    return true;
  } catch {
    return false;
  }
}
