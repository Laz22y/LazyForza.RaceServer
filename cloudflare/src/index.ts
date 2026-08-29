import { RaceCore, type CommandResult, type StoredRaceState } from "./race-core";
import {
  type FlagCommand,
  type LapCompleted,
  type PitServiceCompleted,
  type LoginRequest,
  maximumMessageBytes,
  maximumObservers,
  type ParticipantCommand,
  type DisconnectCommand,
  type PenaltyCommand,
  type PenaltyUpdateCommand,
  type InvestigationCommand,
  protocolVersion,
  type RaceEnvelope,
  type ReadyUpdate,
  type RoomSettings,
  type SessionCommand,
  type TelemetryUpdate
} from "./protocol";
import {
  authenticateControlAccount,
  controlAccountSummaries,
  createControlAccount,
  createStoredCredentials,
  deleteControlAccount,
  maximumControlAccounts,
  normalizeStoredCredentials,
  type ControlAccountRequest,
  type ControlPrincipal,
  type StoredCredentials,
  updateControlAccount,
  verifyPassword
} from "./passwords";
import { controlRoleAllows, requiredControlPermission } from "./control-access";
import {
  bearerToken,
  createPublicTimingAccess,
  publicTimingAccessStatus,
  publicTimingPayload,
  verifyPublicTimingToken
} from "./public-timing";
import { inspectEstateTrackPackage } from "./track-package";
import {
  createRuleTemplate,
  deleteRuleTemplate,
  maximumRuleTemplates,
  normalizeRuleTemplates,
  roomSettingsFromRuleTemplate,
  type RaceRuleTemplateSaveRequest,
  type RaceRuleTemplateSnapshot,
  updateRuleTemplate
} from "./rule-templates";
import {
  activateEventProject,
  captureEventProject,
  copyEventProject,
  createEventProject,
  eventProjectContentDisposition,
  exportEventProjectPackage,
  importEventProjectPackage,
  maximumEventProjectPackageBytes,
  maximumEventProjects,
  normalizeEventProjects,
  setEventProjectStatus,
  summarizeEventProject,
  syncActiveEventProject,
  type EventProjectAssetSnapshot,
  type EventProjectAssets,
  type EventProjectContext,
  type EventProjectSaveRequest,
  type EventProjectSnapshot
} from "./event-projects";

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
  isObserver?: boolean;
}

const adminCookieName = "lfz_race_admin";
const storedStateKey = "race-state-v1";
const storedCredentialsKey = "race-credentials-v1";
const hostedTrackPackageKey = "hosted-track-package-v1";
const hostedTrackPackageMetadataKey = "hosted-track-package-metadata-v1";
const maximumHostedTrackPackageBytes = 1_572_864;
const organizerLogoKey = "organizer-logo-v1";
const organizerLogoMetadataKey = "organizer-logo-metadata-v1";
const maximumOrganizerLogoBytes = 262_144;
const ruleTemplatesKey = "race-rule-templates-v1";
const eventProjectsKey = "race-event-projects-v1";
const roomName = "main";

interface HostedTrackPackageMetadata {
  trackId: string;
  trackName: string;
  trackRevision: string | null;
  trackPackageHash: string;
  fileSha256: string;
  sizeBytes: number;
  uploadedAt: string;
  fileName: string;
}

interface OrganizerLogoMetadata {
  sha256: string;
  mimeType: "image/png" | "image/jpeg";
  sizeBytes: number;
  uploadedAt: string;
  fileName: string;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const room = env.RACE_ROOM.get(env.RACE_ROOM.idFromName(roomName));

    if (url.pathname === "/ws") return room.fetch(request);
    if (url.pathname === "/health" || url.pathname === "/.well-known/lazyforza-race.json")
      return withSecurityHeaders(await room.fetch(request));

    if (url.pathname.startsWith("/api/setup") || url.pathname.startsWith("/api/admin/") ||
        url.pathname.startsWith("/api/public/") ||
        url.pathname === "/api/track-package" || url.pathname === "/api/organizer-logo")
      return withSecurityHeaders(await room.fetch(request));

    return withSecurityHeaders(await env.ASSETS.fetch(request));
  }
} satisfies ExportedHandler<Env>;

export class RaceRoom {
  private core!: RaceCore;
  private readonly initialized: Promise<void>;
  private serverSequence = 0;
  private lastBroadcastAt = 0;
  private lastCoreTickAt = 0;
  private lastTelemetryPersistedAt = 0;
  private credentials: StoredCredentials | null = null;
  private hostedTrackPackage: HostedTrackPackageMetadata | null = null;
  private organizerLogo: OrganizerLogoMetadata | null = null;
  private ruleTemplates: RaceRuleTemplateSnapshot[] = [];
  private eventProjects: EventProjectSnapshot[] = [];
  private setupInProgress = false;

  constructor(private readonly state: DurableObjectState, private readonly env: Env) {
    this.initialized = this.state.blockConcurrencyWhile(async () => {
      const stored = await this.state.storage.get<StoredRaceState>(storedStateKey);
      const loadedCredentials = await this.state.storage.get<StoredCredentials>(storedCredentialsKey) ?? null;
      if (loadedCredentials) {
        const normalized = normalizeStoredCredentials(loadedCredentials);
        this.credentials = normalized.credentials;
        if (normalized.changed) await this.state.storage.put(storedCredentialsKey, this.credentials);
      } else this.credentials = null;
      this.hostedTrackPackage = await this.state.storage.get<HostedTrackPackageMetadata>(hostedTrackPackageMetadataKey) ?? null;
      this.organizerLogo = await this.state.storage.get<OrganizerLogoMetadata>(organizerLogoMetadataKey) ?? null;
      this.ruleTemplates = normalizeRuleTemplates(
        await this.state.storage.get<RaceRuleTemplateSnapshot[]>(ruleTemplatesKey));
      this.eventProjects = normalizeEventProjects(
        await this.state.storage.get<EventProjectSnapshot[]>(eventProjectsKey));
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
      const hosted = this.matchingHostedTrackPackage(snapshot.trackId, snapshot.trackRevision, snapshot.trackPackageHash);
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
        trackPackageAvailable: hosted !== null,
        trackPackageSizeBytes: hosted?.sizeBytes ?? null,
        trackPackageDownloadPath: hosted ? "/api/track-package" : null,
        trackPackageFileSha256: hosted?.fileSha256 ?? null,
        organizerLogoHash: this.organizerLogo?.sha256 ?? null,
        organizerLogoMimeType: this.organizerLogo?.mimeType ?? null,
        organizerLogoDownloadPath: this.organizerLogo ? "/api/organizer-logo" : null,
        supportsObservers: true,
        maximumObservers,
        phase: snapshot.phase,
        serverTime: snapshot.serverTime
      });
    }
    if (url.pathname === "/api/track-package" && request.method === "GET")
      return this.downloadTrackPackage();
    if (url.pathname === "/api/organizer-logo" && request.method === "GET")
      return this.downloadOrganizerLogo();
    if (url.pathname === "/api/public/timing" && request.method === "GET")
      return this.publicTiming(request);
    let controlPrincipal: ControlPrincipal | null = null;
    if (url.pathname.startsWith("/api/admin/")) {
      controlPrincipal = await this.adminPrincipal(request);
      if (!controlPrincipal) return json({ error: "总控登录已过期。" }, 401);
      const permission = requiredControlPermission(request.method, url.pathname);
      if (!controlRoleAllows(controlPrincipal.role, permission))
        return json({ error: "当前总控角色没有执行此操作的权限。" }, 403);
    }
    if (url.pathname === "/api/admin/me" && request.method === "GET")
      return json({ principal: controlPrincipal });
    if (url.pathname === "/api/admin/control-accounts" && request.method === "GET")
      return json({
        accounts: this.currentControlAccountSummaries(),
        maximumAccounts: maximumControlAccounts
      });
    if (url.pathname === "/api/admin/control-accounts" && request.method === "POST")
      return this.createControlAccount(request);
    if (url.pathname === "/api/admin/public-timing" && request.method === "GET")
      return json(publicTimingAccessStatus(this.credentials?.publicTiming));
    if (url.pathname === "/api/admin/public-timing" && request.method === "POST")
      return this.rotatePublicTiming();
    if (url.pathname === "/api/admin/public-timing" && request.method === "DELETE")
      return this.disablePublicTiming();
    const controlAccountRoute = url.pathname.match(/^\/api\/admin\/control-accounts\/([^/]+)$/);
    if (controlAccountRoute && request.method === "PUT")
      return this.updateControlAccount(decodeURIComponent(controlAccountRoute[1]), request);
    if (controlAccountRoute && request.method === "DELETE")
      return this.deleteControlAccount(decodeURIComponent(controlAccountRoute[1]));
    if (url.pathname === "/api/admin/state" && request.method === "GET")
      return json(this.core.snapshot());
    if (url.pathname === "/api/admin/events" && request.method === "GET") {
      const afterText = url.searchParams.get("after");
      const after = afterText === null ? undefined : Number.parseInt(afterText, 10);
      return json(this.core.events(Number.parseInt(url.searchParams.get("limit") ?? "250", 10), after));
    }
    if (url.pathname === "/api/admin/results" && request.method === "GET")
      return json(this.core.results());
    if (url.pathname === "/api/admin/event-projects" && request.method === "GET")
      return json({
        projects: [...this.eventProjects]
          .sort((left, right) => (left.status === "active" ? -1 : right.status === "active" ? 1 :
            right.updatedAt.localeCompare(left.updatedAt)))
          .map(summarizeEventProject),
        maximumProjects: maximumEventProjects
      });
    if (url.pathname === "/api/admin/event-projects" && request.method === "POST")
      return this.createEventProject(request);
    if (url.pathname === "/api/admin/event-projects/import" && request.method === "POST")
      return this.importEventProject(request);
    const eventProjectExportRoute = url.pathname.match(/^\/api\/admin\/event-projects\/([^/]+)\/export$/);
    if (eventProjectExportRoute && request.method === "GET")
      return this.exportEventProject(decodeURIComponent(eventProjectExportRoute[1]));
    const eventProjectActionRoute = url.pathname.match(/^\/api\/admin\/event-projects\/([^/]+)\/(activate|copy|complete|archive)$/);
    if (eventProjectActionRoute && request.method === "POST")
      return this.applyEventProjectAction(
        decodeURIComponent(eventProjectActionRoute[1]), eventProjectActionRoute[2], request);
    const eventProjectRoute = url.pathname.match(/^\/api\/admin\/event-projects\/([^/]+)$/);
    if (eventProjectRoute && request.method === "GET") {
      const project = this.eventProjects.find(item => item.id === decodeURIComponent(eventProjectRoute[1]));
      return project ? json({ project }) : json({ error: "赛事项目不存在。" }, 404);
    }
    if (eventProjectRoute && request.method === "PUT")
      return this.captureEventProject(decodeURIComponent(eventProjectRoute[1]), request);
    if (eventProjectRoute && request.method === "DELETE")
      return this.deleteEventProject(decodeURIComponent(eventProjectRoute[1]));
    const collisionReplayRoute = url.pathname.match(/^\/api\/admin\/investigations\/([^/]+)\/replay$/);
    if (collisionReplayRoute && request.method === "GET") {
      const replay = this.core.collisionReplay(decodeURIComponent(collisionReplayRoute[1]));
      return replay ? json(replay) : json({ error: "碰撞回放证据不存在。" }, 404);
    }
    if (url.pathname === "/api/admin/track-package" && request.method === "GET")
      return json({ package: this.hostedTrackPackage, maximumBytes: maximumHostedTrackPackageBytes });
    if (url.pathname === "/api/admin/track-package" && request.method === "POST")
      return this.uploadTrackPackage(request);
    if (url.pathname === "/api/admin/track-package" && request.method === "DELETE")
      return this.deleteTrackPackage();
    if (url.pathname === "/api/admin/organizer-logo" && request.method === "GET")
      return json({ logo: this.organizerLogo, maximumBytes: maximumOrganizerLogoBytes });
    if (url.pathname === "/api/admin/organizer-logo" && request.method === "POST")
      return this.uploadOrganizerLogo(request);
    if (url.pathname === "/api/admin/organizer-logo" && request.method === "DELETE")
      return this.deleteOrganizerLogo();
    if (url.pathname === "/api/admin/settings" && request.method === "GET")
      return json(this.core.roomSettings());
    if (url.pathname === "/api/admin/settings" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applyRoomSettings(body as RoomSettings));
    if (url.pathname === "/api/admin/rule-templates" && request.method === "GET")
      return json({
        templates: [...this.ruleTemplates].sort((left, right) =>
          right.updatedAt.localeCompare(left.updatedAt) || left.name.localeCompare(right.name)),
        maximumTemplates: maximumRuleTemplates
      });
    if (url.pathname === "/api/admin/rule-templates" && request.method === "POST")
      return this.saveRuleTemplate(request);
    const applyRuleTemplateRoute = url.pathname.match(/^\/api\/admin\/rule-templates\/([^/]+)\/apply$/);
    if (applyRuleTemplateRoute && request.method === "POST")
      return this.applyRuleTemplate(decodeURIComponent(applyRuleTemplateRoute[1]));
    const ruleTemplateRoute = url.pathname.match(/^\/api\/admin\/rule-templates\/([^/]+)$/);
    if (ruleTemplateRoute && request.method === "PUT")
      return this.saveRuleTemplate(request, decodeURIComponent(ruleTemplateRoute[1]));
    if (ruleTemplateRoute && request.method === "DELETE")
      return this.removeRuleTemplate(decodeURIComponent(ruleTemplateRoute[1]));
    if (url.pathname === "/api/admin/collision-investigations" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.setAutomaticCollisionInvestigations(
        Boolean((body as { enabled?: boolean }).enabled)));
    if (url.pathname === "/api/admin/session" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applySession(body as SessionCommand));
    if (url.pathname === "/api/admin/flag" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applyFlag(body as FlagCommand));
    if (url.pathname === "/api/admin/penalty" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applyPenalty(body as PenaltyCommand));
    if (url.pathname === "/api/admin/penalty/update" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.updatePenalty(body as PenaltyUpdateCommand));
    if (url.pathname === "/api/admin/investigation" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.resolveInvestigation(body as InvestigationCommand));
    if (url.pathname === "/api/admin/participant" && request.method === "POST")
      return this.applyAdmin(request, body => this.core.applyParticipant(body as ParticipantCommand));
    if (url.pathname === "/api/admin/disconnect" && request.method === "POST")
      return this.disconnectClient(request);
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
      if (attachment.isObserver) {
        this.send(webSocket, "error", {
          code: "observerReadOnly",
          message: "OB 只接收赛事数据，不能上传遥测或参与比赛流程。"
        });
        return;
      }

      let result: CommandResult;
      let lapAcknowledgement: { eventId: string; isAccepted: boolean; message?: string | null } | null = null;
      let pitServiceAcknowledgement: { eventId: string; isAccepted: boolean; message?: string | null } | null = null;
      // Durable Object alarms may be delivered late. Active client traffic also
      // advances the race clock, but doing so once per telemetry packet makes
      // all 12 drivers pay for the same clock transition checks. Ten checks per
      // second retain sub-frame flag/session responsiveness without that cost.
      const messageNow = Date.now();
      let important = false;
      if (messageNow - this.lastCoreTickAt >= 100) {
        important = this.core.tick(new Date(messageNow));
        this.lastCoreTickAt = messageNow;
      }
      if (envelope.type === "ready") {
        result = this.core.setReady(attachment.participantId, envelope.payload as ReadyUpdate);
        important = true;
      } else if (envelope.type === "telemetry") {
        result = this.core.updateTelemetry(attachment.participantId, envelope.payload as TelemetryUpdate);
      } else if (envelope.type === "lapCompleted") {
        const completed = envelope.payload as LapCompleted;
        result = this.core.completeLap(attachment.participantId, completed);
        lapAcknowledgement = {
          eventId: completed.eventId,
          isAccepted: result.ok,
          message: result.ok ? null : result.error
        };
        important = true;
      } else if (envelope.type === "pitServiceCompleted") {
        const completed = envelope.payload as PitServiceCompleted;
        result = this.core.completePitService(attachment.participantId, completed);
        pitServiceAcknowledgement = {
          eventId: completed.eventId,
          isAccepted: result.ok,
          message: result.ok ? null : result.error
        };
        important = true;
      } else {
        result = { ok: false, error: "未知消息类型。" };
      }

      if (!result.ok) {
        if (important) {
          await this.persist();
          await this.scheduleAlarm();
          this.broadcastSnapshot(true);
        }
        if (lapAcknowledgement)
          this.send(webSocket, "lapAcknowledged", lapAcknowledgement);
        else if (pitServiceAcknowledgement)
          this.send(webSocket, "pitServiceAcknowledged", pitServiceAcknowledgement);
        else
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
      if (lapAcknowledgement)
        this.send(webSocket, "lapAcknowledged", lapAcknowledgement);
      else if (pitServiceAcknowledgement)
        this.send(webSocket, "pitServiceAcknowledged", pitServiceAcknowledgement);
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
      await this.scheduleAlarm();
      this.broadcastSnapshot(true);
    }
  }

  async webSocketError(webSocket: WebSocket): Promise<void> {
    await this.webSocketClose(webSocket, 1011, "WebSocket error");
  }

  async alarm(): Promise<void> {
    await this.initialized;
    this.lastCoreTickAt = Date.now();
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
      const principal = await this.authenticateAdminPassword(body.password ?? "");
      if (!principal) return json({ error: "总控密码不正确。" }, 401);
      const expires = Date.now() + 12 * 60 * 60 * 1_000;
      const accountVersion = this.controlAccountSessionVersion(principal.id);
      if (!accountVersion) return json({ error: "总控账号不存在。" }, 401);
      const nonce = crypto.randomUUID().replaceAll("-", "");
      const value = `${expires}.${principal.id}.${accountVersion}.${nonce}`;
      const signature = await hmac(value, this.adminSigningSecret());
      return json({ serverName: this.env.SERVER_NAME, principal }, 200, {
        "Set-Cookie": `${adminCookieName}=${value}.${signature}; Path=/; Max-Age=43200; HttpOnly; Secure; SameSite=Strict`
      });
    } catch {
      return json({ error: "登录请求格式无效。" }, 400);
    }
  }

  private async adminPrincipal(request: Request): Promise<ControlPrincipal | null> {
    if (!this.isConfigured()) return null;
    const cookie = parseCookies(request.headers.get("Cookie"))[adminCookieName];
    if (!cookie) return null;
    const pieces = cookie.split(".");
    if (pieces.length !== 3 && pieces.length !== 5) return null;
    const expires = Number.parseInt(pieces[0], 10);
    if (!Number.isFinite(expires) || expires <= Date.now()) return null;
    if (pieces.length === 5) {
      const expected = await hmac(`${pieces[0]}.${pieces[1]}.${pieces[2]}.${pieces[3]}`, this.adminSigningSecret());
      if (!constantTimeEquals(expected, pieces[4]) ||
          this.controlAccountSessionVersion(pieces[1]) !== pieces[2]) return null;
      return this.controlPrincipalById(pieces[1]);
    }
    const expected = await hmac(`${pieces[0]}.${pieces[1]}`, this.adminSigningSecret());
    return constantTimeEquals(expected, pieces[2]) ? this.primarySuperAdmin() : null;
  }

  private adminSigningSecret(): string {
    return this.credentials?.admin.hash ?? this.env.ADMIN_PASSWORD ?? "unconfigured";
  }

  private async authenticateAdminPassword(password: string): Promise<ControlPrincipal | null> {
    if (this.credentials) return authenticateControlAccount(this.credentials, password);
    return await secureEquals(password, this.env.ADMIN_PASSWORD ?? "") ? this.environmentSuperAdmin() : null;
  }

  private environmentSuperAdmin(): ControlPrincipal {
    return { id: "environment-super-admin", name: "初始超管", role: "superAdmin" };
  }

  private primarySuperAdmin(): ControlPrincipal | null {
    if (!this.credentials) return this.environmentSuperAdmin();
    const account = this.credentials.controlAccounts?.find(item => item.role === "superAdmin");
    return account ? { id: account.id, name: account.name, role: account.role } : null;
  }

  private controlPrincipalById(id: string): ControlPrincipal | null {
    if (!this.credentials)
      return id === "environment-super-admin" ? this.environmentSuperAdmin() : null;
    const account = this.credentials.controlAccounts?.find(item => item.id === id);
    return account ? { id: account.id, name: account.name, role: account.role } : null;
  }

  private controlAccountSessionVersion(id: string): string | null {
    if (!this.credentials) return id === "environment-super-admin" ? "0" : null;
    const account = this.credentials.controlAccounts?.find(item => item.id === id);
    return account ? String(Date.parse(account.updatedAt)) : null;
  }

  private currentControlAccountSummaries() {
    if (this.credentials) return controlAccountSummaries(this.credentials);
    const principal = this.environmentSuperAdmin();
    const timestamp = new Date(0).toISOString();
    return [{ ...principal, createdAt: timestamp, updatedAt: timestamp }];
  }

  private async ensureStoredCredentials(): Promise<StoredCredentials> {
    if (this.credentials) return this.credentials;
    if (!this.env.PLAYER_PASSWORD || !this.env.ADMIN_PASSWORD)
      throw new Error("服务端尚未完成首次设置。");
    const created = await createStoredCredentials(this.env.PLAYER_PASSWORD, this.env.ADMIN_PASSWORD);
    this.credentials = {
      ...created,
      controlAccounts: created.controlAccounts?.map((account, index) =>
        index === 0 ? { ...account, id: "environment-super-admin" } : account)
    };
    await this.state.storage.put(storedCredentialsKey, this.credentials);
    return this.credentials;
  }

  private async publicTiming(request: Request): Promise<Response> {
    if (!await verifyPublicTimingToken(bearerToken(request), this.credentials?.publicTiming))
      return json({ error: "只读计时令牌无效或已停用。" }, 401);
    return json(publicTimingPayload(this.core.snapshot(), this.core.results()), 200, {
      "X-Robots-Tag": "noindex, nofollow"
    });
  }

  private async rotatePublicTiming(): Promise<Response> {
    const credentials = await this.ensureStoredCredentials();
    const created = await createPublicTimingAccess();
    this.credentials = { ...credentials, publicTiming: created.stored };
    await this.state.storage.put(storedCredentialsKey, this.credentials);
    const fragment = `#token=${created.token}`;
    return json({
      enabled: true,
      generatedAt: created.stored.generatedAt,
      viewerPath: `/timing.html${fragment}`,
      broadcastPath: `/timing.html?overlay=1${fragment}`
    });
  }

  private async disablePublicTiming(): Promise<Response> {
    const credentials = await this.ensureStoredCredentials();
    const disabled = credentials.publicTiming !== undefined;
    const { publicTiming: _removed, ...remaining } = credentials;
    this.credentials = remaining;
    await this.state.storage.put(storedCredentialsKey, this.credentials);
    return json({ disabled });
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
    webSocket.serializeAttachment({
      participantId: result.participantId,
      isObserver: result.isObserver
    } satisfies SocketAttachment);
    for (const other of this.authenticatedSockets()) {
      if (other !== webSocket && attachmentOf(other).participantId === result.participantId) {
        this.send(other, "error", {
          code: "connectionReplaced",
          message: result.isObserver ? "该 OB 已从新的连接恢复。" : "该车手已从新的连接恢复比赛。"
        });
        other.close(1000, "Connection replaced");
      }
    }
    await this.persist();
    const snapshot = this.snapshotWithOrganizerLogo();
    this.send(webSocket, "loginAccepted", {
      participantId: result.participantId,
      resumeToken: result.resumeToken,
      snapshot,
      serverTime: snapshot.serverTime,
      isObserver: result.isObserver
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

  private async createControlAccount(request: Request): Promise<Response> {
    try {
      const credentials = await this.ensureStoredCredentials();
      const body = await readJson(request) as ControlAccountRequest;
      const created = await createControlAccount(credentials, body);
      this.credentials = created.credentials;
      await this.state.storage.put(storedCredentialsKey, this.credentials);
      return json({ account: created.account });
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "总控账号创建失败。" }, 400);
    }
  }

  private async updateControlAccount(accountId: string, request: Request): Promise<Response> {
    try {
      const credentials = await this.ensureStoredCredentials();
      const body = await readJson(request) as ControlAccountRequest;
      const updated = await updateControlAccount(credentials, accountId, body);
      this.credentials = updated.credentials;
      await this.state.storage.put(storedCredentialsKey, this.credentials);
      return json({ account: updated.account });
    } catch (error) {
      return json(
        { error: error instanceof Error ? error.message : "总控账号更新失败。" },
        error instanceof RangeError ? 404 : 400);
    }
  }

  private async deleteControlAccount(accountId: string): Promise<Response> {
    try {
      const credentials = await this.ensureStoredCredentials();
      const removed = deleteControlAccount(credentials, accountId);
      if (!removed.deleted) return json({ error: "总控账号不存在。" }, 404);
      this.credentials = removed.credentials;
      await this.state.storage.put(storedCredentialsKey, this.credentials);
      return json({ ok: true });
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "总控账号删除失败。" }, 400);
    }
  }

  private async saveRuleTemplate(request: Request, templateId?: string): Promise<Response> {
    try {
      const body = await readJson(request) as RaceRuleTemplateSaveRequest;
      const saved = templateId
        ? updateRuleTemplate(this.ruleTemplates, templateId, body)
        : createRuleTemplate(this.ruleTemplates, body);
      this.ruleTemplates = saved.templates;
      await this.state.storage.put(ruleTemplatesKey, this.ruleTemplates);
      return json({ template: saved.template });
    } catch (error) {
      const status = error instanceof RangeError ? 404 : 400;
      return json({ error: error instanceof Error ? error.message : "规则模板保存失败。" }, status);
    }
  }

  private async applyRuleTemplate(templateId: string): Promise<Response> {
    const template = this.ruleTemplates.find(item => item.id === templateId);
    if (!template) return json({ error: "规则模板不存在。" }, 404);
    const result = this.core.applyRoomSettings(
      roomSettingsFromRuleTemplate(template, this.core.roomSettings()));
    if (!result.ok) return json({ error: result.error }, 400);
    await this.persist();
    await this.scheduleAlarm();
    this.broadcastSnapshot(true);
    return json({ template, room: this.core.roomSettings() });
  }

  private async removeRuleTemplate(templateId: string): Promise<Response> {
    const removed = deleteRuleTemplate(this.ruleTemplates, templateId);
    if (!removed.deleted) return json({ error: "规则模板不存在。" }, 404);
    this.ruleTemplates = removed.templates;
    await this.state.storage.put(ruleTemplatesKey, this.ruleTemplates);
    return json({ ok: true });
  }

  private async createEventProject(request: Request): Promise<Response> {
    try {
      const body = await readJson(request) as EventProjectSaveRequest;
      const capture = await this.currentEventProjectCapture();
      const created = createEventProject(this.eventProjects, body, capture.context);
      this.eventProjects = created.projects;
      await this.saveEventProjectAssets(created.project.id, capture.assets);
      await this.state.storage.put(eventProjectsKey, this.eventProjects);
      return json({ project: created.project });
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "赛事项目创建失败。" }, 400);
    }
  }

  private async captureEventProject(projectId: string, request: Request): Promise<Response> {
    try {
      const body = await readJson(request) as EventProjectSaveRequest;
      const capture = await this.currentEventProjectCapture();
      const captured = captureEventProject(this.eventProjects, projectId, body, capture.context);
      this.eventProjects = captured.projects;
      await this.saveEventProjectAssets(captured.project.id, capture.assets);
      await this.state.storage.put(eventProjectsKey, this.eventProjects);
      return json({ project: captured.project });
    } catch (error) {
      const status = error instanceof RangeError ? 404 : 400;
      return json({ error: error instanceof Error ? error.message : "赛事项目保存失败。" }, status);
    }
  }

  private async applyEventProjectAction(
    projectId: string,
    action: string,
    request: Request): Promise<Response> {
    try {
      if (action === "copy") {
        const body = await readJson(request) as { name?: string | null };
        const assets = await this.readEventProjectAssets(projectId);
        const copied = copyEventProject(this.eventProjects, projectId, body.name);
        this.eventProjects = copied.projects;
        await this.saveEventProjectAssets(copied.project.id, assets);
        await this.state.storage.put(eventProjectsKey, this.eventProjects);
        return json({ project: copied.project });
      }
      if (action === "activate") return this.activateEventProject(projectId);
      const synchronized = syncActiveEventProject(this.eventProjects, this.core.results(), this.core.events(500));
      this.eventProjects = synchronized.projects;
      const changed = setEventProjectStatus(
        this.eventProjects, projectId, action === "archive" ? "archived" : "completed");
      this.eventProjects = changed.projects;
      await this.state.storage.put(eventProjectsKey, this.eventProjects);
      return json({ project: changed.project });
    } catch (error) {
      const status = error instanceof RangeError ? 404 : 400;
      return json({ error: error instanceof Error ? error.message : "赛事项目操作失败。" }, status);
    }
  }

  private async activateEventProject(projectId: string): Promise<Response> {
    if (!["lobby", "finished"].includes(this.core.snapshot().phase))
      return json({ error: "练习赛、排位赛或正赛进行期间不能切换赛事项目。请先返回大厅。" }, 400);
    const project = this.eventProjects.find(item => item.id === projectId);
    if (!project) return json({ error: "赛事项目不存在。" }, 404);
    if (project.status === "archived")
      return json({ error: "已归档的赛事项目不能直接启用，请先复制为新项目。" }, 400);
    const assets = await this.readEventProjectAssets(projectId);
    const result = this.core.applyRoomSettings(project.room);
    if (!result.ok) return json({ error: result.error }, 400);

    if (project.trackPackage && assets.trackPackage) {
      if (!project.room.trackId || !project.room.trackName || !project.room.trackPackageHash)
        return json({ error: "赛事项目的赛道标识不完整。" }, 400);
      const metadata: HostedTrackPackageMetadata = {
        trackId: project.room.trackId,
        trackName: project.room.trackName,
        trackRevision: project.room.trackRevision ?? null,
        trackPackageHash: project.room.trackPackageHash,
        fileSha256: project.trackPackage.sha256,
        sizeBytes: project.trackPackage.sizeBytes,
        uploadedAt: new Date().toISOString(),
        fileName: project.trackPackage.fileName
      };
      await this.state.storage.put({
        [hostedTrackPackageKey]: assets.trackPackage,
        [hostedTrackPackageMetadataKey]: metadata
      });
      this.hostedTrackPackage = metadata;
    } else {
      await this.state.storage.delete([hostedTrackPackageKey, hostedTrackPackageMetadataKey]);
      this.hostedTrackPackage = null;
    }

    if (project.organizerLogo && assets.organizerLogo) {
      const metadata: OrganizerLogoMetadata = {
        sha256: project.organizerLogo.sha256,
        mimeType: project.organizerLogo.mimeType as "image/png" | "image/jpeg",
        sizeBytes: project.organizerLogo.sizeBytes,
        uploadedAt: new Date().toISOString(),
        fileName: project.organizerLogo.fileName
      };
      await this.state.storage.put({
        [organizerLogoKey]: assets.organizerLogo,
        [organizerLogoMetadataKey]: metadata
      });
      this.organizerLogo = metadata;
    } else {
      await this.state.storage.delete([organizerLogoKey, organizerLogoMetadataKey]);
      this.organizerLogo = null;
    }

    const activated = activateEventProject(this.eventProjects, projectId);
    this.eventProjects = activated.projects;
    await this.persist();
    await this.scheduleAlarm();
    this.broadcastSnapshot(true);
    return json({ project: activated.project, room: this.core.roomSettings() });
  }

  private async deleteEventProject(projectId: string): Promise<Response> {
    const project = this.eventProjects.find(item => item.id === projectId);
    if (!project) return json({ error: "赛事项目不存在。" }, 404);
    if (project.status === "active") return json({ error: "正在使用的赛事项目不能删除。" }, 400);
    this.eventProjects = this.eventProjects.filter(item => item.id !== projectId);
    await this.state.storage.delete([
      eventProjectTrackKey(projectId), eventProjectLogoKey(projectId)
    ]);
    await this.state.storage.put(eventProjectsKey, this.eventProjects);
    return json({ ok: true });
  }

  private async exportEventProject(projectId: string): Promise<Response> {
    try {
      const synchronized = syncActiveEventProject(this.eventProjects, this.core.results(), this.core.events(500));
      this.eventProjects = synchronized.projects;
      if (synchronized.changed) await this.state.storage.put(eventProjectsKey, this.eventProjects);
      const project = this.eventProjects.find(item => item.id === projectId);
      if (!project) return json({ error: "赛事项目不存在。" }, 404);
      const bytes = await exportEventProjectPackage(project, await this.readEventProjectAssets(projectId));
      return new Response(bytes, { headers: {
        "Content-Type": "application/vnd.lazyforza.event-project",
        "Content-Length": String(bytes.byteLength),
        "Content-Disposition": eventProjectContentDisposition(project),
        "Cache-Control": "private, no-store",
        "X-Content-Type-Options": "nosniff"
      }});
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "赛事项目导出失败。" }, 400);
    }
  }

  private async importEventProject(request: Request): Promise<Response> {
    try {
      const form = await request.formData();
      const file = form.get("file");
      if (!(file instanceof File)) return json({ error: "请选择要导入的 .lfzevent 文件。" }, 400);
      if (file.size <= 0 || file.size > maximumEventProjectPackageBytes)
        return json({ error: "赛事项目包为空或超过 4 MiB 上限。" }, 400);
      if (this.eventProjects.length >= maximumEventProjects)
        return json({ error: `赛事项目最多保存 ${maximumEventProjects} 个。` }, 400);
      const imported = await importEventProjectPackage(
        await file.arrayBuffer(), new Set(this.eventProjects.map(item => item.id)));
      this.eventProjects = [...this.eventProjects, imported.project];
      await this.saveEventProjectAssets(imported.project.id, imported.assets);
      await this.state.storage.put(eventProjectsKey, this.eventProjects);
      return json({ project: imported.project });
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "赛事项目导入失败。" }, 400);
    }
  }

  private async currentEventProjectCapture(): Promise<{
    context: EventProjectContext;
    assets: EventProjectAssets;
  }> {
    const room = this.core.roomSettings();
    const hostedTrack = this.matchingHostedTrackPackage(
      room.trackId, room.trackRevision, room.trackPackageHash);
    const [trackPackage, organizerLogo] = await Promise.all([
      hostedTrack ? this.state.storage.get<ArrayBuffer>(hostedTrackPackageKey) : Promise.resolve(undefined),
      this.organizerLogo ? this.state.storage.get<ArrayBuffer>(organizerLogoKey) : Promise.resolve(undefined)
    ]);
    const validTrack = hostedTrack && trackPackage && trackPackage.byteLength === hostedTrack.sizeBytes &&
      (await sha256Hex(trackPackage)).toLowerCase() === hostedTrack.fileSha256.toLowerCase()
      ? trackPackage : null;
    const validLogo = this.organizerLogo && organizerLogo && organizerLogo.byteLength === this.organizerLogo.sizeBytes &&
      (await sha256Hex(organizerLogo)).toLowerCase() === this.organizerLogo.sha256.toLowerCase()
      ? organizerLogo : null;
    const assets = { trackPackage: validTrack, organizerLogo: validLogo };
    const context: EventProjectContext = {
      room,
      results: this.core.results(),
      events: this.core.events(500),
      trackPackage: hostedTrack && assets.trackPackage ? {
        packagePath: "track/current.lfzestate",
        fileName: hostedTrack.fileName,
        mimeType: "application/vnd.lazyforza.estate-track",
        sha256: hostedTrack.fileSha256,
        sizeBytes: hostedTrack.sizeBytes
      } : null,
      organizerLogo: this.organizerLogo && assets.organizerLogo ? {
        packagePath: this.organizerLogo.mimeType === "image/png"
          ? "assets/organizer-logo.png" : "assets/organizer-logo.jpg",
        fileName: this.organizerLogo.fileName,
        mimeType: this.organizerLogo.mimeType,
        sha256: this.organizerLogo.sha256,
        sizeBytes: this.organizerLogo.sizeBytes
      } : null
    };
    return { context, assets };
  }

  private async readEventProjectAssets(projectId: string): Promise<EventProjectAssets> {
    const project = this.eventProjects.find(item => item.id === projectId);
    if (!project) throw new RangeError("赛事项目不存在。");
    const [trackPackage, organizerLogo] = await Promise.all([
      this.state.storage.get<ArrayBuffer>(eventProjectTrackKey(projectId)),
      this.state.storage.get<ArrayBuffer>(eventProjectLogoKey(projectId))
    ]);
    if (project.trackPackage && (!trackPackage || trackPackage.byteLength !== project.trackPackage.sizeBytes ||
        (await sha256Hex(trackPackage)).toLowerCase() !== project.trackPackage.sha256.toLowerCase()))
      throw new Error("赛事项目素材文件的长度或摘要不一致。");
    if (project.organizerLogo && (!organizerLogo || organizerLogo.byteLength !== project.organizerLogo.sizeBytes ||
        (await sha256Hex(organizerLogo)).toLowerCase() !== project.organizerLogo.sha256.toLowerCase()))
      throw new Error("赛事项目素材文件的长度或摘要不一致。");
    return { trackPackage: trackPackage ?? null, organizerLogo: organizerLogo ?? null };
  }

  private async saveEventProjectAssets(projectId: string, assets: EventProjectAssets): Promise<void> {
    const values: Record<string, ArrayBuffer> = {};
    const deletions: string[] = [];
    if (assets.trackPackage) values[eventProjectTrackKey(projectId)] = assets.trackPackage;
    else deletions.push(eventProjectTrackKey(projectId));
    if (assets.organizerLogo) values[eventProjectLogoKey(projectId)] = assets.organizerLogo;
    else deletions.push(eventProjectLogoKey(projectId));
    if (Object.keys(values).length > 0) await this.state.storage.put(values);
    if (deletions.length > 0) await this.state.storage.delete(deletions);
  }

  private async disconnectClient(request: Request): Promise<Response> {
    try {
      const command = await readJson(request) as DisconnectCommand;
      const clientId = cleanSetupText(command.clientId, 80) ?? "";
      const result = this.core.disconnectAndReleaseClient(clientId);
      if (!result.ok) return json({ error: result.error }, 400);
      for (const webSocket of this.authenticatedSockets()) {
        if (attachmentOf(webSocket).participantId !== clientId) continue;
        try { webSocket.close(1008, "Disconnected by race control"); } catch { /* already closed */ }
      }
      await this.persist();
      await this.scheduleAlarm();
      this.broadcastSnapshot(true);
      return json({ ok: true });
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "请求格式无效。" }, 400);
    }
  }

  private matchingHostedTrackPackage(
    trackId: string | null | undefined,
    trackRevision: string | null | undefined,
    trackPackageHash: string | null | undefined): HostedTrackPackageMetadata | null {
    const hosted = this.hostedTrackPackage;
    if (!hosted || !trackId || !trackPackageHash) return null;
    if (hosted.trackId.toLowerCase() !== trackId.toLowerCase() ||
        hosted.trackPackageHash.toLowerCase() !== trackPackageHash.toLowerCase()) return null;
    if (trackRevision && hosted.trackRevision && hosted.trackRevision !== trackRevision) return null;
    return hosted;
  }

  private async uploadTrackPackage(request: Request): Promise<Response> {
    try {
      if (!["lobby", "finished"].includes(this.core.snapshot().phase))
        return json({ error: "排位赛或正赛进行期间不能更换赛事赛道。请先返回大厅。" }, 400);
      const form = await request.formData();
      const file = form.get("file");
      if (!(file instanceof File)) return json({ error: "请选择要托管的 .lfzestate 文件。" }, 400);
      if (file.size <= 0 || file.size > maximumHostedTrackPackageBytes)
        return json({ error: "赛道文件为空或超过 1.5 MiB 托管上限。" }, 400);
      const bytes = await file.arrayBuffer();
      const identity = await inspectEstateTrackPackage(bytes);
      const metadata: HostedTrackPackageMetadata = {
        trackId: identity.trackId,
        trackName: identity.trackName,
        trackRevision: identity.trackRevision,
        trackPackageHash: identity.trackPackageHash,
        fileSha256: await sha256Hex(bytes),
        sizeBytes: bytes.byteLength,
        uploadedAt: new Date().toISOString(),
        fileName: safeTrackFileName(file.name, identity.trackName)
      };
      await this.state.storage.put({
        [hostedTrackPackageKey]: bytes,
        [hostedTrackPackageMetadataKey]: metadata
      });
      this.hostedTrackPackage = metadata;
      const current = this.core.roomSettings();
      const applied = this.core.applyRoomSettings({
        ...current,
        trackName: identity.trackName,
        trackId: identity.trackId,
        trackRevision: identity.trackRevision,
        trackPackageHash: identity.trackPackageHash
      });
      if (!applied.ok) return json({ error: applied.error }, 400);
      await this.persist();
      this.broadcastSnapshot(true);
      return json({ package: metadata, room: this.core.roomSettings() });
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "赛道文件上传失败。" }, 400);
    }
  }

  private async deleteTrackPackage(): Promise<Response> {
    await this.state.storage.delete([hostedTrackPackageKey, hostedTrackPackageMetadataKey]);
    this.hostedTrackPackage = null;
    return json({ ok: true });
  }

  private async uploadOrganizerLogo(request: Request): Promise<Response> {
    try {
      const form = await request.formData();
      const file = form.get("file");
      if (!(file instanceof File)) return json({ error: "请选择 PNG 或 JPEG 图片。" }, 400);
      if (file.size <= 0 || file.size > maximumOrganizerLogoBytes)
        return json({ error: "赛事 Logo 为空或超过 256 KiB 上限。" }, 400);
      const bytes = await file.arrayBuffer();
      const signature = new Uint8Array(bytes, 0, Math.min(8, bytes.byteLength));
      const png = signature.length >= 8 &&
        [0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a].every((value, index) => signature[index] === value);
      const jpeg = signature.length >= 3 && signature[0] === 0xff && signature[1] === 0xd8 && signature[2] === 0xff;
      if (!png && !jpeg) return json({ error: "赛事 Logo 只支持 PNG 或 JPEG 图片。" }, 400);
      const mimeType: "image/png" | "image/jpeg" = png ? "image/png" : "image/jpeg";
      const metadata: OrganizerLogoMetadata = {
        sha256: await sha256Hex(bytes), mimeType, sizeBytes: bytes.byteLength,
        uploadedAt: new Date().toISOString(),
        fileName: safeLogoFileName(file.name, mimeType)
      };
      await this.state.storage.put({ [organizerLogoKey]: bytes, [organizerLogoMetadataKey]: metadata });
      this.organizerLogo = metadata;
      this.broadcastSnapshot(true);
      return json({ logo: metadata });
    } catch (error) {
      return json({ error: error instanceof Error ? error.message : "赛事 Logo 上传失败。" }, 400);
    }
  }

  private async deleteOrganizerLogo(): Promise<Response> {
    await this.state.storage.delete([organizerLogoKey, organizerLogoMetadataKey]);
    this.organizerLogo = null;
    this.broadcastSnapshot(true);
    return json({ ok: true });
  }

  private async downloadOrganizerLogo(): Promise<Response> {
    const logo = this.organizerLogo;
    if (!logo) return json({ error: "当前房间使用默认 LF Logo。" }, 404);
    const bytes = await this.state.storage.get<ArrayBuffer>(organizerLogoKey);
    if (!bytes || bytes.byteLength !== logo.sizeBytes) return json({ error: "赛事 Logo 不存在或长度不一致。" }, 404);
    return new Response(bytes, { headers: {
      "Content-Type": logo.mimeType, "Content-Length": String(bytes.byteLength),
      "ETag": `"${logo.sha256}"`, "Cache-Control": "public, max-age=86400, immutable",
      "X-Content-Type-Options": "nosniff"
    }});
  }

  private async downloadTrackPackage(): Promise<Response> {
    const snapshot = this.core.snapshot();
    const hosted = this.matchingHostedTrackPackage(snapshot.trackId, snapshot.trackRevision, snapshot.trackPackageHash);
    if (!hosted) return json({ error: "当前房间没有可下载的匹配赛道文件。" }, 404);
    const bytes = await this.state.storage.get<ArrayBuffer>(hostedTrackPackageKey);
    if (!bytes || bytes.byteLength !== hosted.sizeBytes)
      return json({ error: "托管的赛道文件不存在或长度不一致。" }, 404);
    return new Response(bytes, {
      headers: {
        "Content-Type": "application/vnd.lazyforza.estate-track",
        "Content-Length": String(bytes.byteLength),
        "Content-Disposition": `attachment; filename="${hosted.fileName.replaceAll('"', '')}"`,
        "ETag": `"${hosted.fileSha256}"`,
        "Cache-Control": "private, max-age=60",
        "X-Content-Type-Options": "nosniff"
      }
    });
  }

  private send(webSocket: WebSocket, type: string, payload: unknown): void {
    if (webSocket.readyState !== WebSocket.OPEN) return;
    try {
      webSocket.send(JSON.stringify({
        protocolVersion,
        type,
        sequence: ++this.serverSequence,
        payload
      } satisfies RaceEnvelope));
    } catch {
      // The close callback owns participant state; one stale socket must not abort a request.
    }
  }

  private broadcastSnapshot(important: boolean): void {
    const now = Date.now();
    if (!important && now - this.lastBroadcastAt < 100) return;
    this.lastBroadcastAt = now;
    const message = JSON.stringify({
      protocolVersion,
      type: "snapshot",
      sequence: ++this.serverSequence,
      payload: this.snapshotWithOrganizerLogo(new Date(now))
    } satisfies RaceEnvelope);
    for (const webSocket of this.authenticatedSockets()) {
      try { webSocket.send(message); } catch { /* close callback handles state */ }
    }
  }

  private snapshotWithOrganizerLogo(now = new Date()) {
    return {
      ...this.core.snapshot(now),
      organizerLogoHash: this.organizerLogo?.sha256 ?? null,
      organizerLogoMimeType: this.organizerLogo?.mimeType ?? null,
      organizerLogoDownloadPath: this.organizerLogo ? "/api/organizer-logo" : null
    };
  }

  private authenticatedSockets(): WebSocket[] {
    return this.state.getWebSockets().filter(webSocket => Boolean(attachmentOf(webSocket).participantId));
  }

  private hasAnotherSocket(participantId: string, closed: WebSocket): boolean {
    return this.authenticatedSockets().some(webSocket =>
      webSocket !== closed && attachmentOf(webSocket).participantId === participantId);
  }

  private async persist(): Promise<void> {
    const synchronized = syncActiveEventProject(this.eventProjects, this.core.results(), this.core.events(500));
    this.eventProjects = synchronized.projects;
    await this.state.storage.put({
      [storedStateKey]: this.core.serialize(),
      [eventProjectsKey]: this.eventProjects
    });
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

function eventProjectTrackKey(projectId: string): string {
  return `race-event-project:${projectId}:track`;
}

function eventProjectLogoKey(projectId: string): string {
  return `race-event-project:${projectId}:logo`;
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

async function sha256Hex(bytes: ArrayBuffer): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return [...digest].map(value => value.toString(16).padStart(2, "0")).join("").toUpperCase();
}

function safeTrackFileName(fileName: string, trackName: string): string {
  const source = (fileName || `${trackName}.lfzestate`).replace(/[\\/:*?"<>|\u0000-\u001f]/g, "-");
  return source.toLowerCase().endsWith(".lfzestate") ? source : `${source}.lfzestate`;
}

function safeLogoFileName(fileName: string, mimeType: "image/png" | "image/jpeg"): string {
  const extension = mimeType === "image/png" ? ".png" : ".jpg";
  const base = (fileName || `organizer-logo${extension}`).replace(/[\\/:*?"<>|\u0000-\u001f]/g, "-");
  return `${base.replace(/\.[^.]+$/, "")}${extension}`;
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
