export interface EstateTrackPackageIdentity {
  trackId: string;
  trackName: string;
  trackRevision: string;
  trackPackageHash: string;
  payloadSha256: string;
}

interface ZipEntry {
  method: number;
  compressedSize: number;
  uncompressedSize: number;
  localHeaderOffset: number;
}

const manifestName = "manifest.json";
const trackName = "track.json";
const maximumManifestBytes = 1024 * 1024;
const maximumTrackBytes = 48 * 1024 * 1024;
const decoder = new TextDecoder("utf-8", { fatal: true });

export async function inspectEstateTrackPackage(bytes: ArrayBuffer): Promise<EstateTrackPackageIdentity> {
  const archive = new Uint8Array(bytes);
  const entries = readCentralDirectory(archive);
  if (entries.size !== 2 || !entries.has(manifestName) || !entries.has(trackName))
    throw new Error("赛道包结构不正确，只应包含 manifest.json 和 track.json。");
  const manifestBytes = await readEntry(archive, entries.get(manifestName)!, maximumManifestBytes);
  const payloadBytes = await readEntry(archive, entries.get(trackName)!, maximumTrackBytes);
  let manifest: Record<string, unknown>, payload: Record<string, unknown>;
  try {
    manifest = JSON.parse(decoder.decode(manifestBytes)) as Record<string, unknown>;
    payload = JSON.parse(decoder.decode(payloadBytes)) as Record<string, unknown>;
  } catch {
    throw new Error("赛道包中的 JSON 无法读取。");
  }
  if (manifest.format !== "lazyforza-estate-track" || manifest.formatVersion !== 1)
    throw new Error("这不是当前服务端支持的 LazyForza 地产环道文件。");
  const trackId = requiredString(manifest.trackId, "trackId");
  const packageTrackName = requiredString(manifest.trackName, "trackName");
  const trackRevision = requiredString(manifest.mapRevision, "mapRevision");
  const payloadSha256 = requiredSha256(manifest.payloadSha256, "payloadSha256");
  const computedPayloadSha256 = await sha256Hex(payloadBytes);
  if (computedPayloadSha256 !== payloadSha256)
    throw new Error("赛道包内部 SHA-256 校验失败。");
  const fingerprint = manifest.trackFingerprintSha256 === null || manifest.trackFingerprintSha256 === undefined
    ? payloadSha256
    : requiredSha256(manifest.trackFingerprintSha256, "trackFingerprintSha256");
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(trackId))
    throw new Error("赛道包中的 trackId 不是有效 UUID。");

  const payloadTrack = objectValue(payload.track, "track");
  const definition = objectValue(payload.definition, "definition");
  if (requiredString(payloadTrack.id, "track.id").toLowerCase() !== trackId.toLowerCase() ||
      requiredString(payloadTrack.name, "track.name") !== packageTrackName ||
      requiredString(definition.mapRevision, "definition.mapRevision") !== trackRevision)
    throw new Error("赛道包清单与 track.json 内容不一致。");
  return { trackId, trackName: packageTrackName, trackRevision, trackPackageHash: fingerprint, payloadSha256 };
}

function readCentralDirectory(bytes: Uint8Array): Map<string, ZipEntry> {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const minimum = Math.max(0, bytes.byteLength - 65_557);
  let eocd = -1;
  for (let offset = bytes.byteLength - 22; offset >= minimum; offset--) {
    if (uint32(view, offset) === 0x06054b50) { eocd = offset; break; }
  }
  if (eocd < 0) throw new Error("这不是有效的 .lfzestate ZIP 文件。");
  const entryCount = uint16(view, eocd + 10);
  const centralSize = uint32(view, eocd + 12);
  const centralOffset = uint32(view, eocd + 16);
  if (entryCount === 0xffff || centralOffset + centralSize > bytes.byteLength)
    throw new Error("赛道包使用了不支持的 ZIP64 或目录结构。");
  const entries = new Map<string, ZipEntry>();
  let offset = centralOffset;
  for (let index = 0; index < entryCount; index++) {
    if (uint32(view, offset) !== 0x02014b50) throw new Error("赛道包中央目录损坏。");
    const flags = uint16(view, offset + 8);
    const method = uint16(view, offset + 10);
    const compressedSize = uint32(view, offset + 20);
    const uncompressedSize = uint32(view, offset + 24);
    const nameLength = uint16(view, offset + 28);
    const extraLength = uint16(view, offset + 30);
    const commentLength = uint16(view, offset + 32);
    const localHeaderOffset = uint32(view, offset + 42);
    const end = offset + 46 + nameLength + extraLength + commentLength;
    if ((flags & 1) !== 0 || end > bytes.byteLength) throw new Error("赛道包条目已加密或目录越界。");
    const name = decoder.decode(bytes.subarray(offset + 46, offset + 46 + nameLength));
    if (entries.has(name)) throw new Error("赛道包包含重复条目。");
    entries.set(name, { method, compressedSize, uncompressedSize, localHeaderOffset });
    offset = end;
  }
  return entries;
}

async function readEntry(bytes: Uint8Array, entry: ZipEntry, maximumBytes: number): Promise<Uint8Array> {
  if (entry.uncompressedSize > maximumBytes) throw new Error("赛道包解压后超过大小限制。");
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const offset = entry.localHeaderOffset;
  if (uint32(view, offset) !== 0x04034b50) throw new Error("赛道包本地条目损坏。");
  const nameLength = uint16(view, offset + 26);
  const extraLength = uint16(view, offset + 28);
  const start = offset + 30 + nameLength + extraLength;
  const end = start + entry.compressedSize;
  if (end > bytes.byteLength) throw new Error("赛道包条目长度越界。");
  const compressed = bytes.slice(start, end);
  let output: Uint8Array;
  if (entry.method === 0) output = compressed;
  else if (entry.method === 8) {
    const stream = new Blob([compressed]).stream().pipeThrough(new DecompressionStream("deflate-raw"));
    output = new Uint8Array(await new Response(stream).arrayBuffer());
  } else throw new Error("赛道包使用了不支持的 ZIP 压缩方式。");
  if (output.byteLength !== entry.uncompressedSize || output.byteLength > maximumBytes)
    throw new Error("赛道包条目解压长度不一致。");
  return output;
}

function requiredString(value: unknown, name: string): string {
  if (typeof value !== "string" || value.trim().length === 0) throw new Error(`赛道包缺少 ${name}。`);
  return value.trim();
}

function requiredSha256(value: unknown, name: string): string {
  const hash = requiredString(value, name).toUpperCase();
  if (!/^[0-9A-F]{64}$/.test(hash)) throw new Error(`赛道包中的 ${name} 不是有效的 SHA-256。`);
  return hash;
}

function objectValue(value: unknown, name: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error(`赛道包缺少 ${name}。`);
  return value as Record<string, unknown>;
}

function uint16(view: DataView, offset: number): number {
  if (offset < 0 || offset + 2 > view.byteLength) throw new Error("赛道包目录越界。");
  return view.getUint16(offset, true);
}

function uint32(view: DataView, offset: number): number {
  if (offset < 0 || offset + 4 > view.byteLength) throw new Error("赛道包目录越界。");
  return view.getUint32(offset, true);
}

async function sha256Hex(bytes: ArrayBuffer | Uint8Array): Promise<string> {
  const input: ArrayBuffer = bytes instanceof Uint8Array ? Uint8Array.from(bytes).buffer : bytes;
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256", input))]
    .map(value => value.toString(16).padStart(2, "0")).join("").toUpperCase();
}
