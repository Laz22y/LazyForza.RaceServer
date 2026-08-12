import { describe, expect, it } from "vitest";
import { inspectEstateTrackPackage } from "../src/track-package";

const trackId = "11111111-1111-4111-8111-111111111111";

describe("estate track package inspection", () => {
  it("reads the stable fingerprint and identity directly from the package", async () => {
    const fingerprint = "AB".repeat(32);
    const packageBytes = await estatePackage("测试环道", "revision-7", fingerprint);
    await expect(inspectEstateTrackPackage(packageBytes)).resolves.toMatchObject({
      trackId,
      trackName: "测试环道",
      trackRevision: "revision-7",
      trackPackageHash: fingerprint
    });
  });

  it("uses the payload digest for packages created before stable fingerprints", async () => {
    const packageBytes = await estatePackage("旧版环道", "legacy", null);
    const identity = await inspectEstateTrackPackage(packageBytes);
    expect(identity.trackPackageHash).toBe(identity.payloadSha256);
  });

  it("rejects a manifest whose track name differs from track.json", async () => {
    const packageBytes = await estatePackage("清单名称", "1", null, "数据名称");
    await expect(inspectEstateTrackPackage(packageBytes)).rejects.toThrow("清单与 track.json");
  });
});

async function estatePackage(
  manifestName: string,
  revision: string,
  fingerprint: string | null,
  payloadName = manifestName): Promise<ArrayBuffer> {
  const encoder = new TextEncoder();
  const payload = encoder.encode(JSON.stringify({
    track: { id: trackId, name: payloadName },
    sectors: [],
    definition: { trackId, mapRevision: revision }
  }));
  const payloadSha256 = await sha256(payload);
  const manifest = encoder.encode(JSON.stringify({
    format: "lazyforza-estate-track",
    formatVersion: 1,
    trackId,
    trackName: manifestName,
    mapRevision: revision,
    payloadSha256,
    trackFingerprintSha256: fingerprint
  }));
  return buildZip([
    await deflatedEntry("manifest.json", manifest),
    await deflatedEntry("track.json", payload)
  ]).slice().buffer as ArrayBuffer;
}

interface TestZipEntry {
  name: string;
  data: Uint8Array;
  compressed: Uint8Array;
  method: number;
}

async function deflatedEntry(name: string, data: Uint8Array): Promise<TestZipEntry> {
  const stream = new Blob([Uint8Array.from(data)]).stream().pipeThrough(new CompressionStream("deflate-raw"));
  return { name, data, compressed: new Uint8Array(await new Response(stream).arrayBuffer()), method: 8 };
}

function buildZip(entries: TestZipEntry[]): Uint8Array {
  const encoder = new TextEncoder();
  const localParts: Uint8Array[] = [];
  const centralParts: Uint8Array[] = [];
  let localOffset = 0;
  for (const { name, data, compressed, method } of entries) {
    const nameBytes = encoder.encode(name);
    const local = new Uint8Array(30 + nameBytes.length + compressed.length);
    const localView = new DataView(local.buffer);
    localView.setUint32(0, 0x04034b50, true);
    localView.setUint16(4, 20, true);
    localView.setUint16(8, method, true);
    localView.setUint32(18, compressed.length, true);
    localView.setUint32(22, data.length, true);
    localView.setUint16(26, nameBytes.length, true);
    local.set(nameBytes, 30);
    local.set(compressed, 30 + nameBytes.length);
    localParts.push(local);

    const central = new Uint8Array(46 + nameBytes.length);
    const centralView = new DataView(central.buffer);
    centralView.setUint32(0, 0x02014b50, true);
    centralView.setUint16(4, 20, true);
    centralView.setUint16(6, 20, true);
    centralView.setUint16(10, method, true);
    centralView.setUint32(20, compressed.length, true);
    centralView.setUint32(24, data.length, true);
    centralView.setUint16(28, nameBytes.length, true);
    centralView.setUint32(42, localOffset, true);
    central.set(nameBytes, 46);
    centralParts.push(central);
    localOffset += local.length;
  }
  const centralOffset = localOffset;
  const centralSize = centralParts.reduce((sum, value) => sum + value.length, 0);
  const eocd = new Uint8Array(22);
  const view = new DataView(eocd.buffer);
  view.setUint32(0, 0x06054b50, true);
  view.setUint16(8, entries.length, true);
  view.setUint16(10, entries.length, true);
  view.setUint32(12, centralSize, true);
  view.setUint32(16, centralOffset, true);
  return concatenate([...localParts, ...centralParts, eocd]);
}

function concatenate(parts: Uint8Array[]): Uint8Array {
  const output = new Uint8Array(parts.reduce((sum, value) => sum + value.length, 0));
  let offset = 0;
  for (const part of parts) { output.set(part, offset); offset += part.length; }
  return output;
}

async function sha256(value: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", Uint8Array.from(value).buffer);
  return [...new Uint8Array(digest)]
    .map(byte => byte.toString(16).padStart(2, "0")).join("").toUpperCase();
}
