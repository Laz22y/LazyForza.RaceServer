import { RaceCore, type CommandResult, type StoredRaceState } from "./race-core";
import {
  type FlagCommand,
  type LapCompleted,
  type LoginRequest,
  maximumMessageBytes,
  type ParticipantCommand,
  type PenaltyCommand,
  protocolVersion,
  type RaceEnvelope,
  type ReadyUpdate,
  type RoomSettings,
  type SessionCommand,
  type TelemetryUpdate
} from "./protocol";
import {
  createStoredCredentials,
  type StoredCredentials,
  verifyPassword
} from "./passwords";

interface Env {
  RACE_ROOM: DurableObjectNamespace;
  ASSETS: Fetcher;
  PLAYER_PASSWORD?: string;
  ADMIN_PASSWORD?: string;
  SERVER_NAME: string;
  SESSION_NAME: string;
  MAXIMUM_PARTICIPANTS: string;
  TOTAL_RACE_LAPS: string;
}

interface SocketAttachment {
  participantId?: string;
}

const adminCookieName = "lfz_race_admin";
const storedStateKey = "race-state-v1";
const storedCredentialsKey = "race-credentials-v1";
const roomName = "main";

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const room = env.RACE_ROOM.get(env.RACE_ROOM.idFromName(roomName));

    if (url.pathname === "/ws") return room.fetch(request);
    if (url.pathname === "/health" || url.pathname === "/.well-known/lazyforza-race.json")
      return withSecurityHeaders(await room.fetch(request));

    if (url.pathname.startsWith("/api/setup") || url.pathname.startsWith("/api/admin/"))
      return withSecurityHeaders(await room.fetch(request));

    return withSecurityHeaders(await env.ASSETS.fetch(request));
  }
} satisfies ExportedHandler<Env>;

export class RaceRoom {
  private core!: RaceCore;
  private readonly initialized: Promise<void>;
  private serverSequence = 0;
  private lastBroadcastAt = 0;
  private lastTelemetryPersistedAt = 0;
  private credentials: StoredCredentials | null = null;
  private setupInProgress = false;

  constructor(private readonly state: DurableObjectState, private readonly env: Env) {
    this.initialized = this.state.blockConcurrencyWhile(async () => {
      const stored = await this.state.storage.get<StoredRaceState>(storedStateKey);
      this.credentials = await this.state.storage.get<StoredCredentials>(storedCredentialsKey) ?? null;
      this.core = new RaceCore({
        sessionName: env.SESSION_NAME,
        maximumParticipants: Number.parseInt(env.MAXIMUM_PARTICIPANTS, 10),
        totalRaceLaps: Number.parseInt(env.TOTAL_RACE_LAPS, 10)
      }, stored);
      await this.scheduleAlarm();
    });
  }

  async fetch(request: Request): Promise<Response> {
    await this.initialized;
    const url = new URL(request.url);
    if (url.pathname === "/ws") return this.acceptSocket(request);
    if (url.pathname === "/api/setup/status" && request.method === "GET")
      return json({ isConfigured: this.isConfigured(), defaults: this.core.roomSettings() });
    if (url.pathname === "/api/setup" && request.method === "POST")
      return this.initialSetup(request);
    if (url.pathname === "/api/admin/login" && request.method === "POST")
      return this.adminLogin(request);
    if (url.pathname === "/api/admin/logout" && request.method === "POST")
      return adminLogout();
    if (url.pathname === "/health") return json({
      status: "ok",
      serverTime: new Date().toISOString(),
      phase: this.core.snapshot().phase,
      connectedSockets: this.authenticatedSockets().length
    });
    if (url.pathname === "/.well-known/lazyforza-race.json") {
      const snapshot = this.core.snapshot();
      return json({
        serverName: this.env.SERVER_NAME,
        protocolVersion,
        maximumParticipants: Math.min(12, Math.max(1, Number.parseInt(this.env.MAXIMUM_PARTICIPANTS, 10) || 12)),
        requiresPassword: true,
        webSocketPath: "/ws",
        controlPanelPath: "/",
        activeTrackId: snapshot.trackId,
        activeTrackRevision: snapshot.trackRevision,
        activeTrackName: snapshot.trackName,
        activeTrackPackageHash: snapshot.trackPackageHash,
        allowTeams: snapshot.allowTeams,
        sectorCount: snapshot.sectorCount,
        driversPerTeam: snapshot.driversPerTeam,
        teams: snapshot.teams,
        phase: snapshot.phase,
        serverTime: snapshot.serverTime
      });
    }
    if (url.pathname.startsWith("/api/admin/") && !await this.isAdminAuthorized(request))
      return json({ error: "总控登录已过期。" }, 401);
    if (url.pathname === "/api/admin/state" && request.method === "GET")
      return json(this.core.snapshot());
    if (url.pathname === "/api/admin/events" && request.method === "GET")
      return json(this.core.events(Number.parseInt(url.searchParams.get("limit") ?? "250", 10)));
    if (url.pathname === "/api/admin/settings" && request.method === "GET")
      return json(this.core.roomSettings());
    if (url.pathname === "/api/admin/settings" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applyRoomSettings(body as RoomSettings));
    if (url.pathname === "/api/admin/session" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applySession(body as SessionCommand));
    if (url.pathname === "/api/admin/flag" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applyFlag(body as FlagCommand));
    if (url.pathname === "/api/admin/penalty" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applyPenalty(body as PenaltyCommand));
    if (url.pathname === "/api/admin/participant" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applyParticipant(body as ParticipantCommand));
    return json({ error: "Not found" }, 404);
  }

  async webSocketMessage(webSocket: WebSocket, message: string | ArrayBuffer): Promise<void> {
    await this.initialized;
    try {
      const text = typeof message === "string" ? message : new TextDecoder().decode(message);
      if (new TextEncoder().encode(text).byteLength > maximumMessageBytes) {
        webSocket.close(1009, "Message too large");
        return;
      }
      const envelope = JSON.parse(text) as RaceEnvelope;
      if (envelope.protocolVersion !== protocolVersion) {
        this.send(webSocket, "error", { code: "protocolMismatch", message: "客户端与服务端协议版本不一致。" });
        return;
      }
      const attachment = attachmentOf(webSocket);
      if (!attachment.participantId) {
        if (envelope.type !== "login") {
          this.send(webSocket, "loginRejected", { code: "loginRequired", message: "请先登录比赛服务端。" });
          return;
        }
        await this.loginSocket(webSocket, envelope.payload as LoginRequest);
        return;
      }

      if (envelope.type === "ping") {
        const payload = envelope.payload as { clientMonotonicMilliseconds?: number };
        this.send(webSocket, "pong", {
          clientMonotonicMilliseconds: Number(payload?.clientMonotonicMilliseconds) || 0,
          serverUnixMilliseconds: Date.now()
        });
        return;
      }

      let result: CommandResult;
      let important = false;
      if (envelope.type === "ready") {
        result = this.core.setReady(attachment.participantId, envelope.payload as ReadyUpdate);
        important = true;
      } else if (envelope.type === "telemetry") {
        result = this.core.updateTelemetry(attachment.participantId, envelope.payload as TelemetryUpdate);
      } else if (envelope.type === "lapCompleted") {
        result = this.core.completeLap(attachment.participantId, envelope.payload as LapCompleted);
        important = true;
      } else {
        result = { ok: false, error: "未知消息类型。" };
      }

      if (!result.ok) {
        this.send(webSocket, "error", { code: "commandRejected", message: result.error });
        return;
      }
      if (important) {
        await this.persist();
        await this.scheduleAlarm();
        this.broadcastSnapshot(true);
      } else {
        await this.persistTelemetryPeriodically();
        this.broadcastSnapshot(false);
      }
    } catch (error) {
      this.send(webSocket, "error", {
        code: "invalidMessage",
        message: error instanceof Error ? error.message.slice(0, 160) : "消息格式无效。"
      });
    }
  }

  async webSocketClose(webSocket: WebSocket, code: number, reason: string): Promise<void> {
    await this.initialized;
    const participantId = attachmentOf(webSocket).participantId;
    try { webSocket.close(code, reason); } catch { /* already closed */ }
    if (participantId && !this.hasAnotherSocket(participantId, webSocket) && this.core.disconnect(participantId)) {
      await this.persist();
      this.broadcastSnapshot(true);
    }
  }

  async webSocketError(webSocket: WebSocket): Promise<void> {
    await this.webSocketClose(webSocket, 1011, "WebSocket error");
  }

  async alarm(): Promise<void> {
    await this.initialized;
    if (this.core.tick()) {
      await this.persist();
      this.broadcastSnapshot(true);
    }
    await this.scheduleAlarm();
  }

  private isConfigured(): boolean {
    return this.credentials !== null || Boolean(
      this.env.PLAYER_PASSWORD && this.env.ADMIN_PASSWORD &&
      this.env.PLAYER_PASSWORD !== "change-me" && this.env.ADMIN_PASSWORD !== "change-admin-me");
  }

  private async initialSetup(request: Request): Promise<Response> {
    if (this.isConfigured()) return json({ error: "服务端已经完成首次设置。" }, 400);
    if (this.setupInProgress)
      return json({ error: "首次设置正在保存，请不要重复提交。", code: "setupInProgress" }, 409);
    this.setupInProgress = true;
    const previousRoom = this.core.roomSettings();
    try {
      const body = await readJson(request) as {
        playerPassword?: string; adminPassword?: string; sessionName?: string;
        totalRaceLaps?: number; sectorCount?: number;
      };
      const player = body.playerPassword ?? "", admin = body.adminPassword ?? "";
      if (player.length > 128) return json({ error: "房间密码不能超过 128 个字符。" }, 400);
      if (admin.length < 8 || admin.length > 128) return json({ error: "总控密码需要 8–128 个字符。" }, 400);
      if (player === admin) return json({ error: "房间密码和总控密码不能相同。" }, 400);
      const room = this.core.roomSettings();
      const result = this.core.applyRoomSettings({
        ...room,
        sessionName: cleanSetupText(body.sessionName, 64) ?? "地产赛事",
        totalRaceLaps: Number(body.totalRaceLaps) || 10,
        sectorCount: Number(body.sectorCount) || 3
      });
      if (!result.ok) return json({ error: result.error }, 400);
      const credentials = await createStoredCredentials(player, admin);
      await this.state.storage.put({
        [storedCredentialsKey]: credentials,
        [storedStateKey]: this.core.serialize()
      });
      this.credentials = credentials;
      this.lastTelemetryPersistedAt = Date.now();
      return json({ ok: true });
    } catch (error) {
      this.core.applyRoomSettings(previousRoom);
      console.error("Cloudflare initial setup failed", error);
      return json({
        error: "首次设置未能写入 Durable Object，请稍后重试；若仍失败，请查看 Worker 日志中的请求错误。",
        code: "setupFailed"
      }, 503);
    } finally {
      this.setupInProgress = false;
    }
  }

  private async adminLogin(request: Request): Promise<Response> {
    if (!this.isConfigured()) return json({ error: "服务端尚未完成首次设置。" }, 503);
    try {
      const body = await readJson(request) as { password?: string };
      if (!await this.adminPasswordMatches(body.password ?? ""))
        return json({ error: "总控密码不正确。" }, 401);
      const expires = Date.now() + 12 * 60 * 60 * 1_000;
      const nonce = crypto.randomUUID().replaceAll("-", "");
      const value = `${expires}.${nonce}`;
      const signature = await hmac(value, this.adminSigningSecret());
      return json({ serverName: this.env.SERVER_NAME }, 200, {
        "Set-Cookie": `${adminCookieName}=${value}.${signature}; Path=/; Max-Age=43200; HttpOnly; Secure; SameSite=Strict`
      });
    } catch {
      return json({ error: "登录请求格式无效。" }, 400);
    }
  }

  private async isAdminAuthorized(request: Request): Promise<boolean> {
    if (!this.isConfigured()) return false;
    const cookie = parseCookies(request.headers.get("Cookie"))[adminCookieName];
    if (!cookie) return false;
    const pieces = cookie.split(".");
    if (pieces.length !== 3) return false;
    const expires = Number.parseInt(pieces[0], 10);
    if (!Number.isFinite(expires) || expires <= Date.now()) return false;
    const expected = await hmac(`${pieces[0]}.${pieces[1]}`, this.adminSigningSecret());
    return constantTimeEquals(expected, pieces[2]);
  }

  private adminSigningSecret(): string {
    return this.credentials?.admin.hash ?? this.env.ADMIN_PASSWORD ?? "unconfigured";
  }

  private async adminPasswordMatches(password: string): Promise<boolean> {
    return this.credentials
      ? verifyPassword(password, this.credentials.admin)
      : secureEquals(password, this.env.ADMIN_PASSWORD ?? "");
  }

  private async playerPasswordMatches(password: string): Promise<boolean> {
    return this.credentials
      ? verifyPassword(password, this.credentials.player)
      : secureEquals(password, this.env.PLAYER_PASSWORD ?? "");
  }

  private acceptSocket(request: Request): Response {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
      return json({ error: "WebSocket upgrade required" }, 426);
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    server.serializeAttachment({} satisfies SocketAttachment);
    this.state.acceptWebSocket(server);
    return new Response(null, { status: 101, webSocket: client });
  }

  private async loginSocket(webSocket: WebSocket, request: LoginRequest): Promise<void> {
    if (!this.isConfigured() || !await this.playerPasswordMatches(request?.password ?? "")) {
      this.send(webSocket, "loginRejected", { code: "invalidPassword", message: "比赛密码不正确。" });
      return;
    }
    const result = this.core.login(request);
    if (!result.ok) {
      this.send(webSocket, "loginRejected", { code: result.code, message: result.message });
      return;
    }
    webSocket.serializeAttachment({ participantId: result.participantId } satisfies SocketAttachment);
    for (const other of this.authenticatedSockets()) {
      if (other !== webSocket && attachmentOf(other).participantId === result.participantId) {
        this.send(other, "error", { code: "connectionReplaced", message: "该车手已从新的连接恢复比赛。" });
        other.close(1000, "Connection replaced");
      }
    }
    await this.persist();
    const snapshot = this.core.snapshot();
    this.send(webSocket, "loginAccepted", {
      participantId: result.participantId,
      resumeToken: result.resumeToken,
      snapshot,
      serverTime: snapshot.serverTime
    });
    this.broadcastSnapshot(true);
  }

  private async applyAdmin(
    request: Request,
    apply: (body: unknown) => CommandResult): Promise<Response> {
    try {
      const body = await readJson(request);
      const result = apply(body);
      if (!result.ok) return json({ error: result.error }, 400);
      await this.persist();
      await this.scheduleAlarm();
      this.broadcastSnapshot(true);
      return json({ ok: true });
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "请求格式无效。" }, 400);
    }
  }

  private send(webSocket: WebSocket, type: string, payload: unknown): void {
    if (webSocket.readyState !== WebSocket.OPEN) return;
    webSocket.send(JSON.stringify({
      protocolVersion,
      type,
      sequence: ++this.serverSequence,
      payload
    } satisfies RaceEnvelope));
  }

  private broadcastSnapshot(important: boolean): void {
    const now = Date.now();
    if (!important && now - this.lastBroadcastAt < 100) return;
    this.lastBroadcastAt = now;
    const message = JSON.stringify({
      protocolVersion,
      type: "snapshot",
      sequence: ++this.serverSequence,
      payload: this.core.snapshot(new Date(now))
    } satisfies RaceEnvelope);
    for (const webSocket of this.authenticatedSockets()) {
      try { webSocket.send(message); } catch { /* close callback handles state */ }
    }
  }

  private authenticatedSockets(): WebSocket[] {
    return this.state.getWebSockets().filter(webSocket => Boolean(attachmentOf(webSocket).participantId));
  }

  private hasAnotherSocket(participantId: string, closed: WebSocket): boolean {
    return this.authenticatedSockets().some(webSocket =>
      webSocket !== closed && attachmentOf(webSocket).participantId === participantId);
  }

  private async persist(): Promise<void> {
    await this.state.storage.put(storedStateKey, this.core.serialize());
    this.lastTelemetryPersistedAt = Date.now();
  }

  private async persistTelemetryPeriodically(): Promise<void> {
    if (Date.now() - this.lastTelemetryPersistedAt >= 2_000) await this.persist();
  }

  private async scheduleAlarm(): Promise<void> {
    const next = this.core.nextAlarmMilliseconds();
    if (next === null) {
      await this.state.storage.deleteAlarm();
      return;
    }
    await this.state.storage.setAlarm(Math.max(Date.now() + 1, next));
  }
}

function adminLogout(): Response {
  return json({ ok: true }, 200, {
    "Set-Cookie": `${adminCookieName}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Strict`
  });
}

async function secureEquals(left: string, right: string): Promise<boolean> {
  const encoder = new TextEncoder();
  const [leftHash, rightHash] = await Promise.all([
    crypto.subtle.digest("SHA-256", encoder.encode(left)),
    crypto.subtle.digest("SHA-256", encoder.encode(right))
  ]);
  const a = new Uint8Array(leftHash);
  const b = new Uint8Array(rightHash);
  let difference = 0;
  for (let index = 0; index < a.length; index++) difference |= a[index] ^ b[index];
  return difference === 0;
}

function cleanSetupText(value: unknown, maximum: number): string | null {
  if (typeof value !== "string") return null;
  const cleaned = [...value.trim()].filter(character => character >= " " && character !== "\u007f").join("");
  return cleaned.length ? cleaned.slice(0, maximum) : null;
}

async function hmac(value: string, secret: string): Promise<string> {
  const encoder = new TextEncoder();
  const key = await crypto.subtle.importKey(
    "raw", encoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const signature = new Uint8Array(await crypto.subtle.sign("HMAC", key, encoder.encode(value)));
  return base64Url(signature);
}

function base64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function constantTimeEquals(left: string, right: string): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++)
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  return difference === 0;
}

function parseCookies(header: string | null): Record<string, string> {
  if (!header) return {};
  return Object.fromEntries(header.split(";").map(part => {
    const separator = part.indexOf("=");
    return separator < 0
      ? [part.trim(), ""]
      : [part.slice(0, separator).trim(), part.slice(separator + 1).trim()];
  }));
}

function attachmentOf(webSocket: WebSocket): SocketAttachment {
  return (webSocket.deserializeAttachment() as SocketAttachment | null) ?? {};
}

async function readJson(request: Request): Promise<unknown> {
  const contentLength = Number.parseInt(request.headers.get("Content-Length") ?? "0", 10);
  if (contentLength > maximumMessageBytes) throw new Error("请求内容过大。");
  const text = await request.text();
  if (new TextEncoder().encode(text).byteLength > maximumMessageBytes) throw new Error("请求内容过大。");
  return JSON.parse(text);
}

function json(body: unknown, status = 200, headers?: HeadersInit): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", ...headers }
  });
}

function withSecurityHeaders(response: Response): Response {
  const secured = new Response(response.body, response);
  secured.headers.set("X-Content-Type-Options", "nosniff");
  secured.headers.set("X-Frame-Options", "DENY");
  secured.headers.set("Referrer-Policy", "no-referrer");
  secured.headers.set("Content-Security-Policy",
    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' ws: wss:");
  return secured;
}
