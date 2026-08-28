import type { RaceEventSnapshot, RoomSettings, StageResultSnapshot } from "./protocol";
import { inspectEstateTrackPackage } from "./track-package";

export const maximumEventProjects = 64;
export const maximumEventProjectPackageBytes = 4 * 1024 * 1024;
const maximumJsonEntryBytes = 1024 * 1024;
const maximumAuditEvents = 2_000;
const format = "lazyforza-event-project";
const formatVersion = 1;
const manifestPath = "manifest.json";
const eventPath = "event.json";
const schedulePath = "schedule.json";
const rulesPath = "rules.json";
const entrantsPath = "entrants.json";
const resultsPath = "results/stages.json";
const auditPath = "audit/events.json";
const trackPath = "track/current.lfzestate";
const allowedPaths = new Set([
  manifestPath, eventPath, schedulePath, rulesPath, entrantsPath,
  resultsPath, auditPath, trackPath, "assets/organizer-logo.png", "assets/organizer-logo.jpg"
]);
const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });

export type EventProjectStatus = "draft" | "active" | "completed" | "archived";

export interface EventSchedule {
  countdownSeconds: number;
  practiceSessionCount: number;
  practiceSessionMinutes: number[];
  qualifyingSessionCount: number;
  qualifyingSessionMinutes: number[];
  qualifyingEliminationCounts: Array<number | null>;
}

export interface EventProjectSaveRequest {
  name: string;
  shortName?: string | null;
  organizer?: string | null;
  description?: string | null;
  scheduledStartAt?: string | null;
  timeZoneId?: string | null;
  schedule?: Partial<EventSchedule> | null;
}

export interface EventProjectAssetSnapshot {
  packagePath: string;
  fileName: string;
  mimeType: string;
  sha256: string;
  sizeBytes: number;
}

export interface EventProjectAuditSnapshot {
  sequence: number;
  occurredAt: string;
  type: string;
  message: string;
  participantId?: string | null;
}

export interface EventProjectSnapshot {
  id: string;
  name: string;
  shortName?: string | null;
  organizer?: string | null;
  description?: string | null;
  scheduledStartAt?: string | null;
  timeZoneId: string;
  status: EventProjectStatus;
  revision: number;
  createdAt: string;
  updatedAt: string;
  activatedAt?: string | null;
  completedAt?: string | null;
  room: RoomSettings;
  schedule: EventSchedule;
  trackPackage?: EventProjectAssetSnapshot | null;
  organizerLogo?: EventProjectAssetSnapshot | null;
  results: StageResultSnapshot[];
  auditEvents: EventProjectAuditSnapshot[];
}

export interface EventProjectAssets {
  trackPackage: ArrayBuffer | null;
  organizerLogo: ArrayBuffer | null;
}

export interface EventProjectContext {
  room: RoomSettings;
  results: StageResultSnapshot[];
  events: RaceEventSnapshot[];
  trackPackage?: EventProjectAssetSnapshot | null;
  organizerLogo?: EventProjectAssetSnapshot | null;
}

export interface EventProjectSummary {
  id: string;
  name: string;
  shortName?: string | null;
  organizer?: string | null;
  scheduledStartAt?: string | null;
  timeZoneId: string;
  status: EventProjectStatus;
  revision: number;
  createdAt: string;
  updatedAt: string;
  trackName?: string | null;
  resultCount: number;
  entrantCount: number;
  auditEventCount: number;
  hasTrackPackage: boolean;
  hasOrganizerLogo: boolean;
}

export function normalizeEventProjects(value: unknown): EventProjectSnapshot[] {
  if (!Array.isArray(value)) return [];
  const projects: EventProjectSnapshot[] = [];
  for (const candidate of value) {
    if (!candidate || typeof candidate !== "object") continue;
    try { projects.push(normalizeProject(candidate as EventProjectSnapshot)); } catch { /* ignore one invalid record */ }
  }
  return projects.slice(0, maximumEventProjects);
}

export function createEventProject(
  existing: EventProjectSnapshot[],
  request: EventProjectSaveRequest,
  context: EventProjectContext,
  now = new Date()): { projects: EventProjectSnapshot[]; project: EventProjectSnapshot } {
  if (existing.length >= maximumEventProjects) throw new Error(`赛事项目最多保存 ${maximumEventProjects} 个。`);
  const timestamp = now.toISOString();
  const project = buildProject(crypto.randomUUID(), request, context, {
    status: "draft", revision: 1, createdAt: timestamp, updatedAt: timestamp
  });
  return { projects: [...existing, project], project };
}

export function captureEventProject(
  existing: EventProjectSnapshot[],
  id: string,
  request: EventProjectSaveRequest,
  context: EventProjectContext,
  now = new Date()): { projects: EventProjectSnapshot[]; project: EventProjectSnapshot } {
  const index = existing.findIndex(item => item.id === id);
  if (index < 0) throw new RangeError("赛事项目不存在。");
  const previous = existing[index];
  if (previous.status === "archived") throw new Error("已归档的赛事项目不能再修改。");
  const project = buildProject(id, request, context, {
    status: previous.status,
    revision: previous.revision + 1,
    createdAt: previous.createdAt,
    updatedAt: now.toISOString(),
    activatedAt: previous.activatedAt,
    completedAt: previous.completedAt
  });
  const projects = [...existing];
  projects[index] = project;
  return { projects, project };
}

export function copyEventProject(
  existing: EventProjectSnapshot[],
  id: string,
  requestedName?: string | null,
  now = new Date()): { projects: EventProjectSnapshot[]; project: EventProjectSnapshot } {
  if (existing.length >= maximumEventProjects) throw new Error(`赛事项目最多保存 ${maximumEventProjects} 个。`);
  const source = existing.find(item => item.id === id);
  if (!source) throw new RangeError("赛事项目不存在。");
  const timestamp = now.toISOString();
  const project: EventProjectSnapshot = {
    ...structuredClone(source),
    id: crypto.randomUUID(),
    name: cleanRequired(requestedName, 96) ?? `${source.name} - 副本`,
    status: "draft",
    revision: 1,
    createdAt: timestamp,
    updatedAt: timestamp,
    activatedAt: null,
    completedAt: null,
    results: [],
    auditEvents: []
  };
  return { projects: [...existing, project], project };
}

export function activateEventProject(
  existing: EventProjectSnapshot[],
  id: string,
  now = new Date()): { projects: EventProjectSnapshot[]; project: EventProjectSnapshot } {
  const target = existing.find(item => item.id === id);
  if (!target) throw new RangeError("赛事项目不存在。");
  if (target.status === "archived") throw new Error("已归档的赛事项目不能直接启用，请先复制为新项目。");
  const timestamp = now.toISOString();
  const projects = existing.map(item => {
    if (item.id === id) return {
      ...item,
      status: "active" as const,
      revision: item.revision + 1,
      updatedAt: timestamp,
      activatedAt: item.activatedAt ?? timestamp,
      completedAt: null
    };
    if (item.status !== "active") return item;
    const hasResults = item.results.length > 0;
    return {
      ...item,
      status: hasResults ? "completed" as const : "draft" as const,
      revision: item.revision + 1,
      updatedAt: timestamp,
      completedAt: hasResults ? timestamp : null
    };
  });
  return { projects, project: projects.find(item => item.id === id)! };
}

export function setEventProjectStatus(
  existing: EventProjectSnapshot[],
  id: string,
  status: "completed" | "archived",
  now = new Date()): { projects: EventProjectSnapshot[]; project: EventProjectSnapshot } {
  const index = existing.findIndex(item => item.id === id);
  if (index < 0) throw new RangeError("赛事项目不存在。");
  const previous = existing[index];
  if (status === "archived" && previous.status === "active")
    throw new Error("请先完成赛事，再归档项目。");
  const timestamp = now.toISOString();
  const project = {
    ...previous,
    status,
    revision: previous.revision + 1,
    updatedAt: timestamp,
    completedAt: status === "completed" ? timestamp : previous.completedAt
  };
  const projects = [...existing];
  projects[index] = project;
  return { projects, project };
}

export function syncActiveEventProject(
  existing: EventProjectSnapshot[],
  results: StageResultSnapshot[],
  events: RaceEventSnapshot[],
  now = new Date()): { projects: EventProjectSnapshot[]; changed: boolean } {
  const index = existing.findIndex(item => item.status === "active");
  if (index < 0) return { projects: existing, changed: false };
  const project = existing[index];
  const mergedResults = normalizeResults([...project.results, ...results]);
  const mergedEvents = normalizeEvents([...project.auditEvents, ...events]);
  if (JSON.stringify(mergedResults) === JSON.stringify(project.results) &&
      JSON.stringify(mergedEvents) === JSON.stringify(project.auditEvents))
    return { projects: existing, changed: false };
  const projects = [...existing];
  projects[index] = {
    ...project,
    results: mergedResults,
    auditEvents: mergedEvents,
    revision: project.revision + 1,
    updatedAt: now.toISOString()
  };
  return { projects, changed: true };
}

export function summarizeEventProject(project: EventProjectSnapshot): EventProjectSummary {
  const entrants = new Set(project.results.flatMap(result => result.participants.map(item => item.id)));
  return {
    id: project.id,
    name: project.name,
    shortName: project.shortName,
    organizer: project.organizer,
    scheduledStartAt: project.scheduledStartAt,
    timeZoneId: project.timeZoneId,
    status: project.status,
    revision: project.revision,
    createdAt: project.createdAt,
    updatedAt: project.updatedAt,
    trackName: project.room.trackName,
    resultCount: project.results.length,
    entrantCount: entrants.size,
    auditEventCount: project.auditEvents.length,
    hasTrackPackage: project.trackPackage !== null && project.trackPackage !== undefined,
    hasOrganizerLogo: project.organizerLogo !== null && project.organizerLogo !== undefined
  };
}

export async function exportEventProjectPackage(
  project: EventProjectSnapshot,
  assets: EventProjectAssets): Promise<ArrayBuffer> {
  const entrants = [...new Map(project.results.flatMap(result => result.participants).map(item => [item.id, {
    id: item.id, displayName: item.displayName, themeColor: item.themeColor,
    teamName: item.teamName ?? null, teamColor: item.teamColor ?? null
  }])).values()].sort((left, right) => left.displayName.localeCompare(right.displayName));
  const { room, schedule, results, auditEvents, ...event } = project;
  const payloads = new Map<string, Uint8Array>([
    [eventPath, jsonBytes(event)],
    [schedulePath, jsonBytes(schedule)],
    [rulesPath, jsonBytes(room)],
    [entrantsPath, jsonBytes(entrants)],
    [resultsPath, jsonBytes(results)],
    [auditPath, jsonBytes(auditEvents)]
  ]);
  if (project.trackPackage) {
    if (!assets.trackPackage) throw new Error("赛事项目素材文件不存在。");
    await validateAsset(project.trackPackage, assets.trackPackage);
    payloads.set(trackPath, new Uint8Array(assets.trackPackage));
  }
  if (project.organizerLogo) {
    if (!assets.organizerLogo) throw new Error("赛事项目素材文件不存在。");
    await validateAsset(project.organizerLogo, assets.organizerLogo);
    payloads.set(project.organizerLogo.packagePath, new Uint8Array(assets.organizerLogo));
  }
  const entries = await Promise.all([...payloads].map(async ([path, bytes]) => ({
    path, sizeBytes: bytes.byteLength, sha256: await sha256Hex(bytes)
  })));
  const manifest = jsonBytes({
    format, formatVersion, projectId: project.id, exportedAt: new Date().toISOString(), entries
  });
  const archive = makeStoredZip([[manifestPath, manifest], ...payloads]);
  if (archive.byteLength > maximumEventProjectPackageBytes) throw new Error("赛事项目包超过 4 MiB 上限。");
  return archive.buffer.slice(archive.byteOffset, archive.byteOffset + archive.byteLength) as ArrayBuffer;
}

export async function importEventProjectPackage(
  bytes: ArrayBuffer,
  existingIds: ReadonlySet<string> = new Set(),
  now = new Date()): Promise<{ project: EventProjectSnapshot; assets: EventProjectAssets }> {
  if (bytes.byteLength <= 0 || bytes.byteLength > maximumEventProjectPackageBytes)
    throw new Error("赛事项目包为空或超过 4 MiB 上限。");
  const entries = readStoredZip(new Uint8Array(bytes));
  if (entries.size < 7 || entries.size > 10) throw new Error("赛事项目包结构不正确。");
  let total = 0;
  for (const [path, value] of entries) {
    if (!allowedPaths.has(path) || path.includes("\\") || path.startsWith("/") ||
        path.split("/").some(part => part === "" || part === "." || part === ".."))
      throw new Error("赛事项目包包含未知、重复或不安全的文件路径。");
    const maximum = path === trackPath ? 1_572_864 : path.startsWith("assets/") ? 262_144 : maximumJsonEntryBytes;
    if (value.byteLength > maximum) throw new Error("赛事项目包中的文件超过允许大小。");
    total += value.byteLength;
  }
  if (total > maximumEventProjectPackageBytes) throw new Error("赛事项目包解压后超过 4 MiB 上限。");
  const manifest = parseJson<EventManifest>(requiredEntry(entries, manifestPath));
  if (manifest.format !== format || manifest.formatVersion !== formatVersion)
    throw new Error("这不是当前服务端支持的 LazyForza 赛事项目包。");
  if (!Array.isArray(manifest.entries) || manifest.entries.length !== entries.size - 1 ||
      new Set(manifest.entries.map(item => item.path)).size !== manifest.entries.length)
    throw new Error("赛事项目包清单与文件数量不一致。");
  for (const item of manifest.entries) {
    const payload = entries.get(item.path);
    if (!payload || payload.byteLength !== item.sizeBytes ||
        (await sha256Hex(payload)).toLowerCase() !== item.sha256.toLowerCase())
      throw new Error("赛事项目包清单校验失败。");
  }
  const event = parseJson<Omit<EventProjectSnapshot, "room" | "schedule" | "results" | "auditEvents">>(
    requiredEntry(entries, eventPath));
  if (event.id !== manifest.projectId) throw new Error("赛事项目包中的项目标识不一致。");
  const room = parseJson<RoomSettings>(requiredEntry(entries, rulesPath));
  const schedule = normalizeSchedule(parseJson<EventSchedule>(requiredEntry(entries, schedulePath)));
  void parseJson<unknown[]>(requiredEntry(entries, entrantsPath));
  const results = normalizeResults(parseJson<StageResultSnapshot[]>(requiredEntry(entries, resultsPath)));
  const auditEvents = normalizeEvents(parseJson<EventProjectAuditSnapshot[]>(requiredEntry(entries, auditPath)));
  const trackPackage = await readDeclaredAsset(entries, event.trackPackage ?? null, trackPath);
  const organizerLogo = await readDeclaredAsset(entries, event.organizerLogo ?? null, event.organizerLogo?.packagePath ?? null);
  if (!event.organizerLogo && (entries.has("assets/organizer-logo.png") || entries.has("assets/organizer-logo.jpg")))
    throw new Error("赛事项目包包含未声明的素材文件。");
  if (trackPackage) {
    const identity = await inspectEstateTrackPackage(trackPackage);
    if (identity.trackId.toLowerCase() !== (room.trackId ?? "").toLowerCase() ||
        identity.trackRevision !== room.trackRevision ||
        identity.trackPackageHash.toLowerCase() !== (room.trackPackageHash ?? "").toLowerCase())
      throw new Error("赛事项目内的赛道文件与规则快照不一致。");
  }
  if (organizerLogo) validateLogo(organizerLogo, event.organizerLogo!.mimeType);
  const timestamp = now.toISOString();
  const project = normalizeProject({
    ...event,
    id: existingIds.has(event.id) ? crypto.randomUUID() : event.id,
    status: "draft",
    updatedAt: timestamp,
    activatedAt: null,
    completedAt: null,
    room, schedule, results, auditEvents
  } as EventProjectSnapshot);
  return { project, assets: { trackPackage, organizerLogo } };
}

export function eventProjectExportFileName(project: EventProjectSnapshot): string {
  const name = project.name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, "-").trim();
  return `${name || "lazyforza-event"}.lfzevent`;
}

export function eventProjectContentDisposition(project: EventProjectSnapshot): string {
  const encoded = encodeURIComponent(eventProjectExportFileName(project)).replace(
    /['()*]/g, character => `%${character.charCodeAt(0).toString(16).toUpperCase()}`);
  return `attachment; filename="lazyforza-event.lfzevent"; filename*=UTF-8''${encoded}`;
}

type ProjectTimes = Pick<EventProjectSnapshot,
  "status" | "revision" | "createdAt" | "updatedAt" | "activatedAt" | "completedAt">;

function buildProject(
  id: string,
  request: EventProjectSaveRequest,
  context: EventProjectContext,
  times: ProjectTimes): EventProjectSnapshot {
  const name = cleanRequired(request.name, 96);
  if (!name) throw new Error("赛事项目名称不能为空。");
  return {
    id,
    name,
    shortName: cleanOptional(request.shortName, 32),
    organizer: cleanOptional(request.organizer, 96),
    description: cleanOptional(request.description, 1_000),
    scheduledStartAt: normalizeDate(request.scheduledStartAt),
    timeZoneId: cleanOptional(request.timeZoneId, 80) ?? "UTC",
    ...times,
    room: structuredClone(context.room),
    schedule: normalizeSchedule(request.schedule),
    trackPackage: context.trackPackage ?? null,
    organizerLogo: context.organizerLogo ?? null,
    results: normalizeResults(context.results),
    auditEvents: normalizeEvents(context.events)
  };
}

function normalizeProject(project: EventProjectSnapshot): EventProjectSnapshot {
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(project.id))
    throw new Error("赛事项目标识无效。");
  const name = cleanRequired(project.name, 96);
  if (!name || !project.room || typeof project.room !== "object") throw new Error("赛事项目内容无效。");
  const status: EventProjectStatus = ["draft", "active", "completed", "archived"].includes(project.status)
    ? project.status : "draft";
  return {
    ...project,
    name,
    shortName: cleanOptional(project.shortName, 32),
    organizer: cleanOptional(project.organizer, 96),
    description: cleanOptional(project.description, 1_000),
    scheduledStartAt: normalizeDate(project.scheduledStartAt),
    timeZoneId: cleanOptional(project.timeZoneId, 80) ?? "UTC",
    status,
    revision: Math.max(1, Math.trunc(Number(project.revision) || 1)),
    schedule: normalizeSchedule(project.schedule),
    trackPackage: normalizeAsset(project.trackPackage, trackPath),
    organizerLogo: normalizeAsset(project.organizerLogo, null),
    results: normalizeResults(project.results),
    auditEvents: normalizeEvents(project.auditEvents)
  };
}

function normalizeSchedule(value?: Partial<EventSchedule> | null): EventSchedule {
  const practiceSessionCount = clampInteger(value?.practiceSessionCount, 1, 3, 1);
  const qualifyingSessionCount = clampInteger(value?.qualifyingSessionCount, 1, 3, 1);
  return {
    countdownSeconds: clampInteger(value?.countdownSeconds, 0, 120, 10),
    practiceSessionCount,
    practiceSessionMinutes: normalizeMinutes(value?.practiceSessionMinutes, practiceSessionCount, 60),
    qualifyingSessionCount,
    qualifyingSessionMinutes: normalizeMinutes(value?.qualifyingSessionMinutes, qualifyingSessionCount, 10),
    qualifyingEliminationCounts: Array.from({ length: qualifyingSessionCount - 1 }, (_, index) => {
      const candidate = value?.qualifyingEliminationCounts?.[index];
      return candidate === null || candidate === undefined ? null : clampInteger(candidate, 0, 11, 0);
    })
  };
}

function normalizeMinutes(value: number[] | undefined, count: number, fallback: number): number[] {
  return Array.from({ length: count }, (_, index) => clampInteger(value?.[index], 1, 180, fallback));
}

function normalizeResults(values: StageResultSnapshot[] | undefined): StageResultSnapshot[] {
  const byId = new Map<string, StageResultSnapshot>();
  for (const result of Array.isArray(values) ? values : []) {
    if (!result || typeof result.id !== "string") continue;
    byId.set(result.id, result);
  }
  return [...byId.values()].sort((left, right) => Date.parse(left.completedAt) - Date.parse(right.completedAt)).slice(-24);
}

function normalizeEvents(values: Array<EventProjectAuditSnapshot | RaceEventSnapshot> | undefined): EventProjectAuditSnapshot[] {
  const bySequence = new Map<number, EventProjectAuditSnapshot>();
  for (const event of Array.isArray(values) ? values : []) {
    const sequence = Math.trunc(Number(event?.sequence));
    const occurredAt = "occurredAt" in event ? event.occurredAt : new Date().toISOString();
    if (!Number.isFinite(sequence) || typeof occurredAt !== "string" ||
        typeof event.type !== "string" || typeof event.message !== "string") continue;
    bySequence.set(sequence, {
      sequence, occurredAt, type: event.type, message: event.message,
      participantId: event.participantId ?? null
    });
  }
  return [...bySequence.values()].sort((left, right) => left.sequence - right.sequence).slice(-maximumAuditEvents);
}

function normalizeAsset(
  asset: EventProjectAssetSnapshot | null | undefined,
  requiredPath: string | null): EventProjectAssetSnapshot | null {
  if (!asset) return null;
  if ((requiredPath && asset.packagePath !== requiredPath) || !allowedPaths.has(asset.packagePath) ||
      !/^[0-9a-f]{64}$/i.test(asset.sha256) || !Number.isSafeInteger(asset.sizeBytes) || asset.sizeBytes <= 0)
    throw new Error("赛事项目素材清单无效。");
  return asset;
}

async function readDeclaredAsset(
  entries: Map<string, Uint8Array>,
  asset: EventProjectAssetSnapshot | null,
  expectedPath: string | null): Promise<ArrayBuffer | null> {
  if (!asset) {
    if (expectedPath && entries.has(expectedPath)) throw new Error("赛事项目包包含未声明的素材文件。");
    return null;
  }
  if (!expectedPath || asset.packagePath !== expectedPath) throw new Error("赛事项目素材清单校验失败。");
  const bytes = entries.get(expectedPath);
  if (!bytes || bytes.byteLength !== asset.sizeBytes ||
      (await sha256Hex(bytes)).toLowerCase() !== asset.sha256.toLowerCase())
    throw new Error("赛事项目素材清单校验失败。");
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
}

async function validateAsset(asset: EventProjectAssetSnapshot, bytes: ArrayBuffer): Promise<void> {
  if (bytes.byteLength !== asset.sizeBytes ||
      (await sha256Hex(bytes)).toLowerCase() !== asset.sha256.toLowerCase())
    throw new Error("赛事项目素材文件的长度或摘要不一致。");
}

function validateLogo(bytes: ArrayBuffer, mimeType: string): void {
  const signature = new Uint8Array(bytes, 0, Math.min(8, bytes.byteLength));
  const png = signature.length >= 8 &&
    [0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a].every((value, index) => signature[index] === value);
  const jpeg = signature.length >= 3 && signature[0] === 0xff && signature[1] === 0xd8 && signature[2] === 0xff;
  if ((mimeType === "image/png" && !png) || (mimeType === "image/jpeg" && !jpeg) ||
      !["image/png", "image/jpeg"].includes(mimeType))
    throw new Error("赛事项目内的 Logo 文件格式无效。");
}

function jsonBytes(value: unknown): Uint8Array {
  return encoder.encode(JSON.stringify(value, null, 2));
}

function parseJson<T>(bytes: Uint8Array): T {
  try { return JSON.parse(decoder.decode(bytes)) as T; }
  catch { throw new Error("赛事项目包中的 JSON 无法读取。"); }
}

function requiredEntry(entries: Map<string, Uint8Array>, path: string): Uint8Array {
  const value = entries.get(path);
  if (!value) throw new Error(`赛事项目包缺少 ${path}。`);
  return value;
}

function makeStoredZip(entries: Array<[string, Uint8Array]>): Uint8Array {
  const localParts: Uint8Array[] = [];
  const centralParts: Uint8Array[] = [];
  let offset = 0;
  for (const [name, bytes] of entries) {
    const nameBytes = encoder.encode(name);
    const crc = crc32(bytes);
    const local = new Uint8Array(30 + nameBytes.length + bytes.length);
    const localView = new DataView(local.buffer);
    localView.setUint32(0, 0x04034b50, true);
    localView.setUint16(4, 20, true);
    localView.setUint16(6, 0x0800, true);
    localView.setUint16(8, 0, true);
    localView.setUint32(14, crc, true);
    localView.setUint32(18, bytes.length, true);
    localView.setUint32(22, bytes.length, true);
    localView.setUint16(26, nameBytes.length, true);
    local.set(nameBytes, 30);
    local.set(bytes, 30 + nameBytes.length);
    localParts.push(local);

    const central = new Uint8Array(46 + nameBytes.length);
    const centralView = new DataView(central.buffer);
    centralView.setUint32(0, 0x02014b50, true);
    centralView.setUint16(4, 20, true);
    centralView.setUint16(6, 20, true);
    centralView.setUint16(8, 0x0800, true);
    centralView.setUint16(10, 0, true);
    centralView.setUint32(16, crc, true);
    centralView.setUint32(20, bytes.length, true);
    centralView.setUint32(24, bytes.length, true);
    centralView.setUint16(28, nameBytes.length, true);
    centralView.setUint32(42, offset, true);
    central.set(nameBytes, 46);
    centralParts.push(central);
    offset += local.length;
  }
  const centralOffset = offset;
  const centralSize = centralParts.reduce((sum, item) => sum + item.length, 0);
  const end = new Uint8Array(22);
  const endView = new DataView(end.buffer);
  endView.setUint32(0, 0x06054b50, true);
  endView.setUint16(8, entries.length, true);
  endView.setUint16(10, entries.length, true);
  endView.setUint32(12, centralSize, true);
  endView.setUint32(16, centralOffset, true);
  return concatenate([...localParts, ...centralParts, end]);
}

function readStoredZip(archive: Uint8Array): Map<string, Uint8Array> {
  const view = new DataView(archive.buffer, archive.byteOffset, archive.byteLength);
  let endOffset = -1;
  for (let index = archive.length - 22; index >= Math.max(0, archive.length - 65_557); index--) {
    if (view.getUint32(index, true) === 0x06054b50) { endOffset = index; break; }
  }
  if (endOffset < 0) throw new Error("赛事项目包 ZIP 目录无效。");
  const count = view.getUint16(endOffset + 10, true);
  const centralSize = view.getUint32(endOffset + 12, true);
  const centralOffset = view.getUint32(endOffset + 16, true);
  if (count > 10 || centralOffset + centralSize > archive.length) throw new Error("赛事项目包 ZIP 目录无效。");
  const entries = new Map<string, Uint8Array>();
  let cursor = centralOffset;
  for (let index = 0; index < count; index++) {
    if (cursor + 46 > archive.length || view.getUint32(cursor, true) !== 0x02014b50)
      throw new Error("赛事项目包 ZIP 目录无效。");
    const method = view.getUint16(cursor + 10, true);
    const compressedSize = view.getUint32(cursor + 20, true);
    const uncompressedSize = view.getUint32(cursor + 24, true);
    const nameLength = view.getUint16(cursor + 28, true);
    const extraLength = view.getUint16(cursor + 30, true);
    const commentLength = view.getUint16(cursor + 32, true);
    const localOffset = view.getUint32(cursor + 42, true);
    const nameEnd = cursor + 46 + nameLength;
    if (nameEnd > archive.length || localOffset + 30 > archive.length || method !== 0 || compressedSize !== uncompressedSize)
      throw new Error("赛事项目包只支持未压缩的标准 ZIP 条目。");
    const name = decoder.decode(archive.subarray(cursor + 46, nameEnd));
    if (entries.has(name) || view.getUint32(localOffset, true) !== 0x04034b50)
      throw new Error("赛事项目包包含重复文件或损坏的 ZIP 条目。");
    const localNameLength = view.getUint16(localOffset + 26, true);
    const localExtraLength = view.getUint16(localOffset + 28, true);
    const dataOffset = localOffset + 30 + localNameLength + localExtraLength;
    if (dataOffset + compressedSize > archive.length) throw new Error("赛事项目包 ZIP 条目长度无效。");
    entries.set(name, archive.slice(dataOffset, dataOffset + compressedSize));
    cursor = nameEnd + extraLength + commentLength;
  }
  return entries;
}

function concatenate(parts: Uint8Array[]): Uint8Array {
  const output = new Uint8Array(parts.reduce((sum, item) => sum + item.length, 0));
  let offset = 0;
  for (const part of parts) { output.set(part, offset); offset += part.length; }
  return output;
}

function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (const value of bytes) {
    crc ^= value;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}

async function sha256Hex(bytes: ArrayBuffer | Uint8Array): Promise<string> {
  const source = bytes instanceof Uint8Array ? new Uint8Array(bytes).slice().buffer : bytes;
  const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", source));
  return [...hash].map(value => value.toString(16).padStart(2, "0")).join("").toUpperCase();
}

function cleanRequired(value: unknown, maximum: number): string | null {
  return cleanOptional(value, maximum);
}

function cleanOptional(value: unknown, maximum: number): string | null {
  if (typeof value !== "string") return null;
  const cleaned = [...value.trim()].filter(character => character >= " ").join("").slice(0, maximum);
  return cleaned || null;
}

function normalizeDate(value: unknown): string | null {
  if (typeof value !== "string" || !Number.isFinite(Date.parse(value))) return null;
  return new Date(value).toISOString();
}

function clampInteger(value: unknown, minimum: number, maximum: number, fallback: number): number {
  const parsed = Math.trunc(Number(value));
  return Number.isFinite(parsed) ? Math.min(maximum, Math.max(minimum, parsed)) : fallback;
}

interface EventManifest {
  format: string;
  formatVersion: number;
  projectId: string;
  exportedAt: string;
  entries: Array<{ path: string; sizeBytes: number; sha256: string }>;
}
