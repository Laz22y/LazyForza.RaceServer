import {
  type BannerKind,
  type BannerSnapshot,
  type BlueFlagSnapshot,
  clamp,
  clampInteger,
  cleanText,
  type ControlFlag,
  type FlagCommand,
  type GripCondition,
  isThemeColor,
  type LapCompleted,
  type LoginRequest,
  type ParticipantCommand,
  type ParticipantSnapshot,
  type ParticipantStatus,
  type PenaltyCommand,
  type PenaltyKind,
  type PenaltySnapshot,
  type ReadyUpdate,
  type RoomSettings,
  type SessionCommand,
  type SessionPhase,
  type SessionSnapshot,
  type TelemetryUpdate,
  type YellowZoneSnapshot
} from "./protocol";

export interface RaceConfiguration {
  sessionName: string;
  maximumParticipants: number;
  totalRaceLaps: number;
  trackId?: string | null;
  trackRevision?: string | null;
  trackPackageHash?: string | null;
  sectorCount?: number;
  automaticYellowEnabled?: boolean;
  slowSpeedKph?: number;
  slowDurationSeconds?: number;
  severeLateralOffsetMeters?: number;
  recoveryDurationSeconds?: number;
  allowTeams?: boolean;
  trackName?: string | null;
}

interface ParticipantState {
  id: string;
  resumeToken: string;
  displayName: string;
  themeColor: string;
  teamName?: string | null;
  joinedAt: string;
  lastSeenAt: string;
  finishedAt?: string | null;
  status: ParticipantStatus;
  isConnected: boolean;
  isReady: boolean;
  completedLaps: number;
  currentSector: number;
  trackProgress: number;
  lateralOffsetMeters: number;
  mapX: number;
  mapY: number;
  speedKph: number;
  currentLapSeconds: number;
  lastLapSeconds?: number | null;
  bestLapSeconds?: number | null;
  bestSectorSeconds: Array<number | null>;
  isInPitLane: boolean;
  isInServiceZone: boolean;
  pitServiceElapsedSeconds: number;
  pitServiceRequirementMet: boolean;
  completedPitServices: number;
  gripCondition: GripCondition;
  hazardCandidateReason?: string | null;
  hazardCandidateStartedAt?: string | null;
  hazardRecoveryStartedAt?: string | null;
  automaticYellowActive?: boolean;
  automaticYellowSector?: number;
  automaticYellowReason?: string | null;
  qualifyingFinalLapPending?: boolean;
  falseStartArmedAt?: string | null;
  falseStartReferenceProgress?: number | null;
  falseStartMovementStartedAt?: string | null;
  falseStartPenalized?: boolean;
}

export interface StoredRaceState {
  revision: number;
  sessionName: string;
  phase: SessionPhase;
  phaseBeforeSuspension: SessionPhase;
  flag: ControlFlag;
  flagMessage?: string | null;
  totalRaceLaps: number;
  startsAt?: string | null;
  startSequenceAt?: string | null;
  illuminatedStartLights: number;
  startLightsOut: boolean;
  qualifyingEndsAt?: string | null;
  qualifyingTimeExpired: boolean;
  banner?: BannerSnapshot | null;
  participants: ParticipantState[];
  penalties: PenaltySnapshot[];
  receivedLapEvents: string[];
  sectorCount: number;
  automaticYellowEnabled: boolean;
  slowSpeedKph: number;
  slowDurationSeconds: number;
  severeLateralOffsetMeters: number;
  recoveryDurationSeconds: number;
  allowTeams: boolean;
  trackName?: string | null;
  trackId?: string | null;
  trackRevision?: string | null;
  trackPackageHash?: string | null;
  manualFullCourseYellow?: string | null;
  manualSectorYellows: Record<string, string>;
}

export type CommandResult = { ok: true } | { ok: false; error: string };
export type LoginResult =
  | { ok: true; participantId: string; resumeToken: string; resumed: boolean }
  | { ok: false; code: string; message: string };

const allowedGrip = new Set<GripCondition>([
  "unknown", "slightlyReduced", "moderatelyReduced", "severelyReduced", "atLimit"
]);
const allowedFlags = new Set<ControlFlag>(["green", "yellow", "red", "chequered"]);
const allowedPenalties = new Set<PenaltyKind>([
  "warning", "time", "driveThrough", "stopAndGo", "gridDrop", "disqualification"
]);

export class RaceCore {
  private readonly maximumParticipants: number;
  private state: StoredRaceState;

  constructor(configuration: RaceConfiguration, stored?: StoredRaceState | null) {
    this.maximumParticipants = clampInteger(configuration.maximumParticipants, 1, 12);
    this.state = stored ? this.normalizeStored(stored) : {
      revision: 1,
      sessionName: cleanText(configuration.sessionName, 64) ?? "地产赛事",
      phase: "lobby",
      phaseBeforeSuspension: "race",
      flag: "green",
      flagMessage: null,
      totalRaceLaps: clampInteger(configuration.totalRaceLaps, 1, 999),
      startsAt: null,
      startSequenceAt: null,
      illuminatedStartLights: 0,
      startLightsOut: false,
      qualifyingEndsAt: null,
      qualifyingTimeExpired: false,
      banner: null,
      participants: [],
      penalties: [],
      receivedLapEvents: []
      ,sectorCount: clampInteger(configuration.sectorCount ?? 3, 1, 20)
      ,automaticYellowEnabled: configuration.automaticYellowEnabled ?? true
      ,slowSpeedKph: clamp(configuration.slowSpeedKph ?? 12, 3, 50)
      ,slowDurationSeconds: clamp(configuration.slowDurationSeconds ?? 3, 1, 15)
      ,severeLateralOffsetMeters: clamp(configuration.severeLateralOffsetMeters ?? 25, 5, 200)
      ,recoveryDurationSeconds: clamp(configuration.recoveryDurationSeconds ?? 3, 1, 15)
      ,manualFullCourseYellow: null
      ,manualSectorYellows: {}
      ,allowTeams: configuration.allowTeams ?? true
      ,trackName: cleanText(configuration.trackName, 128)
      ,trackId: cleanText(configuration.trackId, 128)
      ,trackRevision: cleanText(configuration.trackRevision, 64)
      ,trackPackageHash: cleanText(configuration.trackPackageHash, 128)
    };
  }

  serialize(): StoredRaceState {
    return structuredClone(this.state);
  }

  login(request: LoginRequest, now = new Date()): LoginResult {
    const displayName = cleanText(request.displayName, 20);
    const teamName = this.state.allowTeams ? cleanText(request.teamName, 24) : null;
    if (!displayName) return { ok: false, code: "invalidProfile", message: "比赛显示名不能为空。" };
    if (!isThemeColor(request.themeColor))
      return { ok: false, code: "invalidProfile", message: "主题色必须使用 #RRGGBB 格式。" };
    if (!this.trackMatches(request))
      return { ok: false, code: "trackMismatch", message: "客户端选择的地产赛道与本场赛事不一致。" };
    if (request.sectorCount !== null && request.sectorCount !== undefined &&
        clampInteger(request.sectorCount, 1, 20) !== this.state.sectorCount)
      return {
        ok: false,
        code: "sectorMismatch",
        message: `客户端赛道为 ${request.sectorCount} 个分段，房间设置为 ${this.state.sectorCount} 个分段。`
      };

    const resumeToken = cleanText(request.resumeToken, 256);
    const resumed = resumeToken
      ? this.state.participants.find(participant => constantTimeTextEquals(participant.resumeToken, resumeToken))
      : undefined;
    if (resumed) {
      if (this.hasDuplicateName(displayName, resumed.id))
        return { ok: false, code: "duplicateName", message: "该比赛显示名已被其他车手使用。" };
      resumed.displayName = displayName;
      resumed.themeColor = request.themeColor.toUpperCase();
      resumed.teamName = teamName;
      resumed.isConnected = true;
      resumed.status = resumed.isReady ? "ready" : "connected";
      resumed.lastSeenAt = now.toISOString();
      this.touch();
      return { ok: true, participantId: resumed.id, resumeToken: resumed.resumeToken, resumed: true };
    }

    if (this.state.participants.length >= this.maximumParticipants)
      return { ok: false, code: "roomFull", message: `本场已达到 ${this.maximumParticipants} 人上限。` };
    if (this.hasDuplicateName(displayName))
      return { ok: false, code: "duplicateName", message: "该比赛显示名已被使用。" };

    const participant: ParticipantState = {
      id: crypto.randomUUID(),
      resumeToken: createResumeToken(),
      displayName,
      themeColor: request.themeColor.toUpperCase(),
      teamName,
      joinedAt: now.toISOString(),
      lastSeenAt: now.toISOString(),
      finishedAt: null,
      status: "connected",
      isConnected: true,
      isReady: false,
      completedLaps: 0,
      currentSector: 0,
      trackProgress: 0,
      lateralOffsetMeters: 0,
      mapX: 0,
      mapY: 0,
      speedKph: 0,
      currentLapSeconds: 0,
      lastLapSeconds: null,
      bestLapSeconds: null,
      bestSectorSeconds: [],
      isInPitLane: false,
      isInServiceZone: false,
      pitServiceElapsedSeconds: 0,
      pitServiceRequirementMet: false,
      completedPitServices: 0,
      gripCondition: "unknown",
      qualifyingFinalLapPending: false,
      falseStartArmedAt: null,
      falseStartReferenceProgress: null,
      falseStartMovementStartedAt: null,
      falseStartPenalized: false
    };
    this.state.participants.push(participant);
    this.touch();
    return { ok: true, participantId: participant.id, resumeToken: participant.resumeToken, resumed: false };
  }

  disconnect(participantId: string, now = new Date()): boolean {
    const participant = this.find(participantId);
    if (!participant || !participant.isConnected) return false;
    participant.isConnected = false;
    participant.status = "disconnected";
    participant.automaticYellowActive = false;
    participant.hazardCandidateStartedAt = null;
    participant.hazardRecoveryStartedAt = null;
    participant.lastSeenAt = now.toISOString();
    participant.qualifyingFinalLapPending = false;
    this.completeQualifyingIfReady(now);
    this.refreshYellowFlag(now);
    this.touch();
    return true;
  }

  setReady(participantId: string, update: ReadyUpdate, now = new Date()): CommandResult {
    const participant = this.find(participantId);
    if (!participant) return rejected("车手不存在。");
    if (this.state.phase !== "lobby" && this.state.phase !== "grid")
      return rejected("当前阶段不能修改准备状态。");
    participant.isReady = Boolean(update.isReady);
    participant.status = participant.isReady ? "ready" : "connected";
    participant.lastSeenAt = now.toISOString();
    this.touch();
    return accepted();
  }

  updateTelemetry(participantId: string, update: TelemetryUpdate, now = new Date()): CommandResult {
    const participant = this.find(participantId);
    if (!participant) return rejected("车手不存在。");
    participant.isConnected = true;
    participant.lastSeenAt = now.toISOString();
    if (!update.isTelemetryValid || update.isPausedOrRewinding) {
      participant.speedKph = 0;
      participant.hazardCandidateStartedAt = null;
      participant.hazardRecoveryStartedAt = null;
      return accepted();
    }

    participant.trackProgress = clamp(update.trackProgress, 0, 1);
    participant.lateralOffsetMeters = clamp(update.lateralOffsetMeters, -1_000, 1_000);
    participant.mapX = clamp(update.mapX, -10_000_000, 10_000_000);
    participant.mapY = clamp(update.mapY, -10_000_000, 10_000_000);
    participant.speedKph = clamp(update.speedKph, 0, 800);
    participant.currentSector = clampInteger(update.currentSector, 0, this.state.sectorCount - 1);
    participant.currentLapSeconds = clamp(update.currentLapSeconds, 0, 7_200);
    participant.isInPitLane = Boolean(update.isInPitLane);
    participant.isInServiceZone = participant.isInPitLane && Boolean(update.isInServiceZone);
    participant.pitServiceElapsedSeconds = participant.isInServiceZone
      ? clamp(update.pitServiceElapsedSeconds, 0, 60)
      : 0;
    participant.pitServiceRequirementMet = participant.isInServiceZone && Boolean(update.pitServiceRequirementMet);
    const reportedServices = clampInteger(update.completedPitServices, 0, 999);
    if (participant.pitServiceRequirementMet && reportedServices === participant.completedPitServices + 1)
      participant.completedPitServices++;
    participant.gripCondition = allowedGrip.has(update.gripCondition) ? update.gripCondition : "unknown";
    if (!terminal(participant.status)) {
      participant.status = participant.isInServiceZone
        ? "inService"
        : participant.isInPitLane ? "inPitLane" : "onTrack";
    }
    this.evaluateFalseStart(participant, now);
    this.evaluateAutomaticYellow(participant, now);
    this.refreshYellowFlag(now);
    // completedLaps is deliberately ignored. Only a unique, valid lap event
    // may advance the server-authoritative counter.
    return accepted();
  }

  completeLap(participantId: string, completed: LapCompleted, now = new Date()): CommandResult {
    const participant = this.find(participantId);
    if (!participant) return rejected("车手不存在。");
    if (this.state.phase !== "qualifying" && this.state.phase !== "race")
      return rejected("当前阶段不接收圈速成绩。");
    if (this.state.phase === "qualifying" && this.state.qualifyingTimeExpired && !participant.qualifyingFinalLapPending)
      return rejected("排位赛计时已结束，该车手没有待完成的最后一圈。");
    const eventId = cleanText(completed.eventId, 80);
    if (!eventId) return rejected("圈速事件编号无效。");
    if (this.state.receivedLapEvents.includes(eventId)) return accepted();
    this.state.receivedLapEvents.push(eventId);
    if (this.state.receivedLapEvents.length > 20_000)
      this.state.receivedLapEvents.splice(0, this.state.receivedLapEvents.length - 10_000);
    if (!completed.isValid) {
      participant.qualifyingFinalLapPending = false;
      this.completeQualifyingIfReady(now);
      this.touch();
      return accepted();
    }
    if (!Number.isFinite(completed.lapSeconds) || completed.lapSeconds < 3 || completed.lapSeconds > 21_600)
      return rejected("圈速数值超出有效范围。");

    const priorFastest = this.fastestLap();
    participant.completedLaps++;
    participant.lastLapSeconds = completed.lapSeconds;
    participant.bestLapSeconds = participant.bestLapSeconds === null || participant.bestLapSeconds === undefined
      ? completed.lapSeconds
      : Math.min(participant.bestLapSeconds, completed.lapSeconds);
    participant.currentLapSeconds = 0;
    participant.currentSector = 0;
    participant.lastSeenAt = now.toISOString();
    this.updateBestSectors(participant, completed.sectorSeconds);
    participant.qualifyingFinalLapPending = false;

    const newFastest = this.fastestLap();
    if (newFastest && (!priorFastest || newFastest.time < priorFastest.time - 0.0005)) {
      this.state.banner = this.newBanner(
        "fastestLap", "全场最快圈", `${newFastest.participant.displayName} · ${formatLap(newFastest.time)}`,
        newFastest.participant.id, 8_000, now);
    }
    if (this.state.phase === "race") {
      if (this.state.flag === "chequered" && !terminal(participant.status)) {
        participant.status = "finished";
        participant.finishedAt = now.toISOString();
      } else if (this.state.flag !== "chequered" &&
                 participant.completedLaps >= this.state.totalRaceLaps &&
                 this.orderParticipants()[0]?.id === participant.id) {
        participant.status = "finished";
        participant.finishedAt = now.toISOString();
        this.state.flag = "chequered";
        this.state.flagMessage = "领跑者已完成预定圈数";
        this.clearYellowState();
        this.state.banner = this.newBanner(
          "winner", "比赛胜者", participant.displayName, participant.id, null, now);
      }
      const classified = this.state.participants.filter(candidate => candidate.isConnected &&
        candidate.status !== "didNotFinish" && candidate.status !== "disqualified" && candidate.status !== "disconnected");
      if (this.state.flag === "chequered" && classified.every(candidate => candidate.status === "finished"))
        this.state.phase = "finished";
    }
    this.completeQualifyingIfReady(now);
    this.touch();
    return accepted();
  }

  roomSettings(): RoomSettings {
    return {
      sessionName: this.state.sessionName,
      totalRaceLaps: this.state.totalRaceLaps,
      sectorCount: this.state.sectorCount,
      automaticYellowEnabled: this.state.automaticYellowEnabled,
      slowSpeedKph: this.state.slowSpeedKph,
      slowDurationSeconds: this.state.slowDurationSeconds,
      severeLateralOffsetMeters: this.state.severeLateralOffsetMeters,
      recoveryDurationSeconds: this.state.recoveryDurationSeconds
      ,allowTeams: this.state.allowTeams
      ,trackName: this.state.trackName
      ,trackId: this.state.trackId
      ,trackRevision: this.state.trackRevision
      ,trackPackageHash: this.state.trackPackageHash
    };
  }

  applyRoomSettings(command: RoomSettings, now = new Date()): CommandResult {
    if (["outLap", "formationLap", "countdown", "race", "suspended"].includes(this.state.phase))
      return rejected("发车后不能修改房间规则。请先返回大厅。");
    const sessionName = cleanText(command.sessionName, 64);
    if (!sessionName) return rejected("赛事名称不能为空。");
    const trackName = cleanText(command.trackName, 128), trackId = cleanText(command.trackId, 128);
    const trackHash = cleanText(command.trackPackageHash, 128)?.toUpperCase() ?? null;
    const hasTrackIdentity = Boolean(trackName || trackId || trackHash);
    if (hasTrackIdentity && (!trackName || !trackId || !trackHash))
      return rejected("配置赛事赛道时，名称、标识和 SHA-256 三项都要填写。");
    if (trackId && !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(trackId))
      return rejected("赛道标识不是有效的 UUID。");
    if (trackHash && !/^[0-9a-f]{64}$/i.test(trackHash))
      return rejected("赛道 SHA-256 必须是导出提示中的 64 位十六进制摘要。");
    this.state.sessionName = sessionName;
    this.state.totalRaceLaps = clampInteger(command.totalRaceLaps, 1, 999);
    this.state.sectorCount = clampInteger(command.sectorCount, 1, 20);
    this.state.automaticYellowEnabled = Boolean(command.automaticYellowEnabled);
    this.state.slowSpeedKph = clamp(command.slowSpeedKph, 3, 50);
    this.state.slowDurationSeconds = clamp(command.slowDurationSeconds, 1, 15);
    this.state.severeLateralOffsetMeters = clamp(command.severeLateralOffsetMeters, 5, 200);
    this.state.recoveryDurationSeconds = clamp(command.recoveryDurationSeconds, 1, 15);
    this.state.allowTeams = command.allowTeams !== false;
    this.state.trackName = trackName;
    this.state.trackId = trackId;
    this.state.trackRevision = cleanText(command.trackRevision, 64);
    this.state.trackPackageHash = trackHash;
    if (!this.state.allowTeams)
      for (const participant of this.state.participants) participant.teamName = null;
    if (!this.state.automaticYellowEnabled) {
      for (const participant of this.state.participants) {
        participant.automaticYellowActive = false;
        participant.hazardCandidateStartedAt = null;
        participant.hazardRecoveryStartedAt = null;
      }
      this.refreshYellowFlag(now);
    }
    this.touch();
    return accepted();
  }

  applySession(command: SessionCommand, now = new Date()): CommandResult {
    const sessionName = cleanText(command.sessionName, 64);
    if (sessionName) this.state.sessionName = sessionName;
    if (command.totalRaceLaps !== null && command.totalRaceLaps !== undefined)
      this.state.totalRaceLaps = clampInteger(command.totalRaceLaps, 1, 999);

    switch (command.phase) {
      case "lobby":
        this.resetCompetitiveState();
        this.state.phase = "lobby";
        this.state.flag = "green";
        this.state.flagMessage = null;
        break;
      case "qualifying":
        this.resetCompetitiveState();
        this.state.phase = "qualifying";
        this.state.flag = "green";
        this.state.qualifyingTimeExpired = false;
        this.state.qualifyingEndsAt = new Date(
          now.getTime() + clampInteger(command.qualifyingMinutes ?? 10, 1, 180) * 60_000).toISOString();
        for (const participant of this.state.participants) {
          participant.status = participant.isConnected ? "onTrack" : "disconnected";
          participant.isReady = false;
        }
        this.state.banner = this.newBanner("information", "排位赛开始", this.state.sessionName, null, 5_000, now);
        break;
      case "grid":
        this.state.phase = "grid";
        this.state.qualifyingEndsAt = null;
        this.state.qualifyingTimeExpired = false;
        this.state.flag = "green";
        for (const participant of this.state.participants.filter(candidate => candidate.isConnected)) {
          participant.status = "ready";
          participant.qualifyingFinalLapPending = false;
        }
        break;
      case "outLap":
        this.prepareRace();
        this.state.phase = "outLap";
        this.state.flag = "green";
        this.state.banner = this.newBanner("information", "出场圈", "按总控指令驶离维修区，前往发车准备位置。", null, 6_000, now);
        break;
      case "formationLap":
        if (this.state.phase !== "outLap") this.prepareRace();
        this.state.phase = "formationLap";
        this.state.flag = "green";
        this.state.banner = this.newBanner("information", "暖胎圈", "保持队列，返回发车位置。", null, 6_000, now);
        break;
      case "countdown":
        this.prepareRace();
        this.state.phase = "countdown";
        this.state.flag = "green";
        this.state.startSequenceAt = new Date(
          now.getTime() + clampInteger(command.countdownSeconds ?? 10, 0, 120) * 1_000).toISOString();
        const randomHoldMilliseconds = randomInteger(1_000, 4_000);
        this.state.startsAt = new Date(Date.parse(this.state.startSequenceAt) + 4_000 + randomHoldMilliseconds).toISOString();
        this.state.illuminatedStartLights = 0;
        this.state.startLightsOut = false;
        this.state.banner = this.newBanner(
          "information", "准备发车", `首盏红灯将在 ${clampInteger(command.countdownSeconds ?? 10, 0, 120)} 秒后亮起`, null,
          Math.max(1_000, Date.parse(this.state.startSequenceAt) - now.getTime()), now);
        break;
      case "race":
        if (this.state.phase !== "countdown") this.prepareRace();
        this.state.phase = "race";
        this.state.startsAt = now.toISOString();
        this.state.startSequenceAt = null;
        this.state.illuminatedStartLights = 0;
        this.state.startLightsOut = true;
        this.state.flag = "green";
        this.state.banner = this.newBanner("information", "比赛开始", this.state.sessionName, null, 4_000, now);
        break;
      case "finished":
        return rejected("方格旗由领跑者完成预定圈数后自动触发。");
      default:
        return rejected("该阶段不能通过常规阶段命令直接设置。");
    }
    this.touch();
    return accepted();
  }

  applyFlag(command: FlagCommand, now = new Date()): CommandResult {
    if (!allowedFlags.has(command.flag)) return rejected("旗语类型无效。");
    if (command.flag === "chequered")
      return rejected("方格旗按领跑者完成预定圈数的规则自动亮起，不能手动发布。");
    const message = cleanText(command.message, 160);
    const requestedSector = command.sectorIndex === null || command.sectorIndex === undefined
      ? null : clampInteger(command.sectorIndex, 0, this.state.sectorCount - 1);
    if (command.flag === "green") {
      if (this.state.phase === "suspended") {
        this.state.phase = this.state.phaseBeforeSuspension;
        this.state.flag = "green";
      }
      if (requestedSector === null) {
        this.state.manualFullCourseYellow = null;
        this.state.manualSectorYellows = {};
      } else delete this.state.manualSectorYellows[String(requestedSector)];
      this.refreshYellowFlag(now);
    } else if (command.flag === "yellow") {
      if (this.state.flag === "red") return rejected("红旗期间不能发布黄旗，请先恢复绿旗。");
      if (requestedSector === null) this.state.manualFullCourseYellow = message ?? "赛道总控发布全场黄旗";
      else this.state.manualSectorYellows[String(requestedSector)] = message ?? "赛道总控发布分区黄旗";
      if (this.state.flag !== "chequered") this.refreshYellowFlag(now);
    } else if (command.flag === "red") {
      if (this.state.phase !== "suspended") this.state.phaseBeforeSuspension = this.state.phase;
      this.state.phase = "suspended";
      this.state.flag = "red";
      this.state.flagMessage = message ?? "比赛暂停";
    }
    this.touch();
    return accepted();
  }

  applyPenalty(command: PenaltyCommand, now = new Date()): CommandResult {
    const participant = this.find(command.participantId);
    if (!participant) return rejected("车手不存在。");
    if (!allowedPenalties.has(command.kind)) return rejected("处罚类型无效。");
    const reason = cleanText(command.reason, 160);
    if (!reason) return rejected("处罚原因不能为空。");
    const penalty: PenaltySnapshot = {
      id: crypto.randomUUID(),
      participantId: participant.id,
      kind: command.kind,
      valueSeconds: command.kind === "time" || command.kind === "stopAndGo"
        ? clamp(command.valueSeconds, 1, 3_600) : null,
      gridPlaces: command.kind === "gridDrop" ? clampInteger(command.gridPlaces, 1, 99) : null,
      reason,
      issuedAt: now.toISOString(),
      isServed: false,
      isRevoked: false
    };
    this.state.penalties.push(penalty);
    if (command.kind === "disqualification") participant.status = "disqualified";
    this.state.banner = this.newBanner(
      "penalty", `处罚 · ${participant.displayName}`, penaltyDescription(penalty), participant.id, 10_000, now);
    this.touch();
    return accepted();
  }

  applyParticipant(command: ParticipantCommand, now = new Date()): CommandResult {
    const participant = this.find(command.participantId);
    if (!participant) return rejected("车手不存在。");
    if (command.status !== "didNotFinish" && command.status !== "disqualified")
      return rejected("总控只能直接标记退赛或取消资格。");
    participant.status = command.status;
    participant.finishedAt = now.toISOString();
    participant.automaticYellowActive = false;
    participant.hazardCandidateStartedAt = null;
    participant.hazardRecoveryStartedAt = null;
    this.refreshYellowFlag(now);
    this.state.banner = this.newBanner(
      command.status === "disqualified" ? "penalty" : "information",
      command.status === "disqualified" ? "取消资格" : "车手退赛",
      `${participant.displayName} · ${cleanText(command.reason, 160) ?? "未填写原因"}`,
      participant.id, 8_000, now);
    this.touch();
    return accepted();
  }

  tick(now = new Date()): boolean {
    let changed = false;
    if (this.state.phase === "countdown" && this.state.startsAt && this.state.startSequenceAt) {
      if (now.getTime() >= Date.parse(this.state.startsAt)) {
        this.state.phase = "race";
        this.state.flag = "green";
        this.state.illuminatedStartLights = 0;
        this.state.startLightsOut = true;
        this.state.banner = this.newBanner("information", "比赛开始", this.state.sessionName, null, 4_000, now);
        changed = true;
      } else {
        const nextLights = calculateIlluminatedStartLights(now, new Date(this.state.startSequenceAt));
        if (nextLights !== this.state.illuminatedStartLights) {
          this.state.illuminatedStartLights = nextLights;
          if (nextLights > 0) this.armFalseStartDetection(now);
          changed = true;
        }
      }
    }
    if (this.state.phase === "qualifying" && !this.state.qualifyingTimeExpired && this.state.qualifyingEndsAt &&
        now.getTime() >= Date.parse(this.state.qualifyingEndsAt)) {
      this.state.flag = "chequered";
      this.state.qualifyingTimeExpired = true;
      for (const participant of this.state.participants)
        participant.qualifyingFinalLapPending = this.eligibleForFinalQualifyingLap(participant);
      const pendingCount = this.state.participants.filter(participant => participant.qualifyingFinalLapPending).length;
      this.state.banner = this.newBanner(
        "chequeredFlag", "排位赛计时结束",
        pendingCount > 0 ? `仍有 ${pendingCount} 名车手可完成最后飞驰圈` : "成绩已冻结", null, 8_000, now);
      this.completeQualifyingIfReady(now);
      changed = true;
    }
    if (this.state.banner?.expiresAt && now.getTime() >= Date.parse(this.state.banner.expiresAt)) {
      this.state.banner = null;
      changed = true;
    }
    if (changed) this.touch();
    return changed;
  }

  nextAlarmMilliseconds(): number | null {
    const values = [
      this.state.phase === "countdown" ? this.state.startsAt : null,
      this.state.phase === "qualifying" && !this.state.qualifyingTimeExpired ? this.state.qualifyingEndsAt : null,
      this.state.banner?.expiresAt
    ]
      .filter((value): value is string => Boolean(value))
      .map(value => Date.parse(value))
      .filter(Number.isFinite);
    if (this.state.phase === "countdown" && this.state.startSequenceAt && this.state.illuminatedStartLights < 5) {
      const sequenceAt = Date.parse(this.state.startSequenceAt);
      values.push(sequenceAt + this.state.illuminatedStartLights * 1_000);
    }
    return values.length === 0 ? null : Math.min(...values);
  }

  snapshot(now = new Date()): SessionSnapshot {
    const ordered = this.orderParticipants();
    let leaderComparable: number | null = null;
    let priorComparable: number | null = null;
    const participants: ParticipantSnapshot[] = ordered.map((participant, index) => {
      const comparable = this.state.phase === "qualifying" || this.state.phase === "grid"
        ? participant.bestLapSeconds ?? null : null;
      if (index === 0) leaderComparable = comparable;
      const result: ParticipantSnapshot = {
        id: participant.id,
        position: index + 1,
        displayName: participant.displayName,
        themeColor: participant.themeColor,
        teamName: participant.teamName,
        status: participant.status,
        isConnected: participant.isConnected,
        isReady: participant.isReady,
        completedLaps: participant.completedLaps,
        currentSector: participant.currentSector,
        trackProgress: participant.trackProgress,
        mapX: participant.mapX,
        mapY: participant.mapY,
        speedKph: participant.speedKph,
        currentLapSeconds: participant.currentLapSeconds,
        lastLapSeconds: participant.lastLapSeconds,
        bestLapSeconds: participant.bestLapSeconds,
        gapToLeaderSeconds: comparable !== null && leaderComparable !== null ? comparable - leaderComparable : null,
        intervalSeconds: comparable !== null && priorComparable !== null ? comparable - priorComparable : null,
        isInPitLane: participant.isInPitLane,
        isInServiceZone: participant.isInServiceZone,
        pitServiceElapsedSeconds: participant.pitServiceElapsedSeconds,
        pitServiceRequirementMet: participant.pitServiceRequirementMet,
        completedPitServices: participant.completedPitServices,
        gripCondition: participant.gripCondition,
        bestSectorSeconds: [...participant.bestSectorSeconds],
        penalties: this.state.penalties.filter(penalty =>
          penalty.participantId === participant.id && !penalty.isRevoked),
        lastSeenAt: participant.lastSeenAt,
        qualifyingFinalLapPending: participant.qualifyingFinalLapPending ?? false
      };
      if (comparable !== null) priorComparable = comparable;
      return result;
    });
    const fastest = this.fastestLap();
    return {
      revision: this.state.revision,
      sessionName: this.state.sessionName,
      phase: this.state.phase,
      flag: this.state.flag,
      flagMessage: this.state.flagMessage,
      trackId: this.state.trackId,
      trackRevision: this.state.trackRevision,
      trackPackageHash: this.state.trackPackageHash,
      totalRaceLaps: this.state.totalRaceLaps,
      startsAt: this.state.startsAt,
      startSequenceAt: this.state.startSequenceAt,
      illuminatedStartLights: this.state.illuminatedStartLights,
      startLightsOut: this.state.startLightsOut,
      qualifyingEndsAt: this.state.qualifyingEndsAt,
      qualifyingTimeExpired: this.state.qualifyingTimeExpired,
      fastestParticipantId: fastest?.participant.id ?? null,
      fastestLapSeconds: fastest?.time ?? null,
      fastestSectorSeconds: this.fastestSectors(),
      banner: this.state.banner?.expiresAt && Date.parse(this.state.banner.expiresAt) <= now.getTime()
        ? null : this.state.banner,
      participants,
      serverTime: now.toISOString(),
      yellowZones: this.yellowZones(),
      sectorCount: this.state.sectorCount,
      allowTeams: this.state.allowTeams,
      trackName: this.state.trackName,
      blueFlags: this.blueFlags()
    };
  }

  private normalizeStored(stored: StoredRaceState): StoredRaceState {
    return {
      ...stored,
      revision: clampInteger(stored.revision, 1, Number.MAX_SAFE_INTEGER),
      sessionName: cleanText(stored.sessionName, 64) ?? "地产赛事",
      phaseBeforeSuspension: stored.phaseBeforeSuspension ?? "race",
      totalRaceLaps: clampInteger(stored.totalRaceLaps, 1, 999),
      startsAt: stored.startsAt ?? null,
      startSequenceAt: stored.startSequenceAt ?? null,
      illuminatedStartLights: clampInteger(stored.illuminatedStartLights ?? 0, 0, 5),
      startLightsOut: stored.startLightsOut ?? false,
      qualifyingEndsAt: stored.qualifyingEndsAt ?? null,
      qualifyingTimeExpired: stored.qualifyingTimeExpired ?? false,
      banner: stored.banner ?? null,
      participants: Array.isArray(stored.participants) ? stored.participants.slice(0, 12).map(participant => ({
        ...participant,
        qualifyingFinalLapPending: participant.qualifyingFinalLapPending ?? false,
        falseStartArmedAt: participant.falseStartArmedAt ?? null,
        falseStartReferenceProgress: participant.falseStartReferenceProgress ?? null,
        falseStartMovementStartedAt: participant.falseStartMovementStartedAt ?? null,
        falseStartPenalized: participant.falseStartPenalized ?? false
      })) : [],
      penalties: Array.isArray(stored.penalties) ? stored.penalties : [],
      receivedLapEvents: Array.isArray(stored.receivedLapEvents) ? stored.receivedLapEvents.slice(-10_000) : []
      ,sectorCount: clampInteger(stored.sectorCount ?? 3, 1, 20)
      ,automaticYellowEnabled: stored.automaticYellowEnabled ?? true
      ,slowSpeedKph: clamp(stored.slowSpeedKph ?? 12, 3, 50)
      ,slowDurationSeconds: clamp(stored.slowDurationSeconds ?? 3, 1, 15)
      ,severeLateralOffsetMeters: clamp(stored.severeLateralOffsetMeters ?? 25, 5, 200)
      ,recoveryDurationSeconds: clamp(stored.recoveryDurationSeconds ?? 3, 1, 15)
      ,manualFullCourseYellow: cleanText(stored.manualFullCourseYellow, 160)
      ,manualSectorYellows: stored.manualSectorYellows && typeof stored.manualSectorYellows === "object"
        ? stored.manualSectorYellows : {}
      ,allowTeams: stored.allowTeams ?? true
      ,trackName: cleanText(stored.trackName, 128)
      ,trackId: cleanText(stored.trackId, 128)
      ,trackRevision: cleanText(stored.trackRevision, 64)
      ,trackPackageHash: cleanText(stored.trackPackageHash, 128)
    };
  }

  private trackMatches(request: LoginRequest): boolean {
    return (!this.state.trackId || equalsIgnoreCase(this.state.trackId, request.trackId)) &&
      (!this.state.trackRevision || this.state.trackRevision === request.trackRevision) &&
      (!this.state.trackPackageHash || equalsIgnoreCase(this.state.trackPackageHash, request.trackPackageHash));
  }

  private hasDuplicateName(displayName: string, exceptId?: string): boolean {
    return this.state.participants.some(participant =>
      participant.id !== exceptId && participant.displayName.localeCompare(displayName, undefined, { sensitivity: "accent" }) === 0);
  }

  private find(participantId: string): ParticipantState | undefined {
    return this.state.participants.find(participant => participant.id === participantId);
  }

  private connectedCount(): number {
    return this.state.participants.filter(participant => participant.isConnected).length;
  }

  private touch(): void { this.state.revision++; }

  private evaluateAutomaticYellow(participant: ParticipantState, now: Date): void {
    if (!this.state.automaticYellowEnabled || (this.state.phase !== "race" && this.state.phase !== "qualifying") || participant.isInPitLane ||
        participant.isInServiceZone || terminal(participant.status) || participant.status === "disconnected") {
      participant.automaticYellowActive = false;
      participant.hazardCandidateStartedAt = null;
      participant.hazardRecoveryStartedAt = null;
      return;
    }
    const severeOffset = Math.abs(participant.lateralOffsetMeters) >= this.state.severeLateralOffsetMeters;
    const extremelySlow = participant.speedKph <= this.state.slowSpeedKph;
    const reason = severeOffset ? "车辆严重偏离赛道" : extremelySlow ? "车辆在赛道上异常低速" : null;
    const requiredMilliseconds = (severeOffset ? 1 : this.state.slowDurationSeconds) * 1_000;
    if (reason) {
      participant.hazardRecoveryStartedAt = null;
      if (participant.hazardCandidateReason !== reason) {
        participant.hazardCandidateReason = reason;
        participant.hazardCandidateStartedAt = now.toISOString();
      }
      participant.hazardCandidateStartedAt ??= now.toISOString();
      if (now.getTime() - Date.parse(participant.hazardCandidateStartedAt) >= requiredMilliseconds) {
        participant.automaticYellowActive = true;
        participant.automaticYellowSector = participant.currentSector;
        participant.automaticYellowReason = reason;
      }
      return;
    }
    participant.hazardCandidateReason = null;
    participant.hazardCandidateStartedAt = null;
    if (!participant.automaticYellowActive) return;
    participant.hazardRecoveryStartedAt ??= now.toISOString();
    if (now.getTime() - Date.parse(participant.hazardRecoveryStartedAt) < this.state.recoveryDurationSeconds * 1_000) return;
    participant.automaticYellowActive = false;
    participant.automaticYellowReason = null;
    participant.hazardRecoveryStartedAt = null;
  }

  private refreshYellowFlag(now: Date): void {
    if (this.state.flag === "red" || this.state.flag === "chequered") return;
    const zones = this.yellowZones();
    const previous = this.state.flag;
    if (zones.length === 0) {
      this.state.flag = "green";
      this.state.flagMessage = null;
      return;
    }
    this.state.flag = "yellow";
    const first = zones.find(zone => zone.sectorIndex === null) ?? zones[0];
    this.state.flagMessage = first.sectorIndex === null || first.sectorIndex === undefined
      ? first.reason : `第 ${first.sectorIndex + 1} 分段 · ${first.reason}`;
  }

  private yellowZones(): YellowZoneSnapshot[] {
    const zones: YellowZoneSnapshot[] = [];
    if (this.state.manualFullCourseYellow)
      zones.push({ sectorIndex: null, isAutomatic: false, reason: this.state.manualFullCourseYellow });
    for (const [sector, reason] of Object.entries(this.state.manualSectorYellows).sort((a,b)=>Number(a[0])-Number(b[0])))
      zones.push({ sectorIndex: Number(sector), isAutomatic: false, reason });
    const automatic = this.state.participants.filter(candidate => candidate.automaticYellowActive).map(participant => ({
        sectorIndex: participant.automaticYellowSector ?? participant.currentSector,
        isAutomatic: true,
        reason: participant.automaticYellowReason ?? "赛道上存在异常车辆",
        participantId: participant.id,
        participantName: participant.displayName
      } satisfies YellowZoneSnapshot));
    if (new Set(automatic.map(zone => zone.sectorIndex)).size >= 2)
      zones.push({ sectorIndex: null, isAutomatic: true, reason: "多个分段同时存在异常车辆" });
    zones.push(...automatic);
    return zones;
  }

  private blueFlags(): BlueFlagSnapshot[] {
    if (this.state.phase !== "race") return [];
    const active = this.state.participants.filter(participant => participant.isConnected &&
      !participant.isInPitLane && !participant.isInServiceZone && !terminal(participant.status));
    const flags: BlueFlagSnapshot[] = [];
    for (const approaching of active) for (const recipient of active) {
      if (approaching.id === recipient.id || approaching.completedLaps < recipient.completedLaps + 1) continue;
      let distanceAhead = recipient.trackProgress - approaching.trackProgress;
      if (distanceAhead < 0) distanceAhead += 1;
      if (distanceAhead > .003 && distanceAhead <= .15)
        flags.push({ recipientParticipantId: recipient.id, approachingParticipantId: approaching.id, distanceAhead });
    }
    return flags;
  }

  private armFalseStartDetection(now: Date): void {
    for (const participant of this.state.participants) {
      participant.falseStartArmedAt = now.toISOString();
      participant.falseStartReferenceProgress = participant.trackProgress;
      participant.falseStartMovementStartedAt = null;
      participant.falseStartPenalized = false;
    }
  }

  private evaluateFalseStart(participant: ParticipantState, now: Date): void {
    if (this.state.phase !== "countdown" || !this.state.startSequenceAt || !this.state.startsAt ||
        now.getTime() < Date.parse(this.state.startSequenceAt) || now.getTime() >= Date.parse(this.state.startsAt) ||
        participant.falseStartPenalized || participant.isInPitLane || participant.isInServiceZone ||
        participant.status === "disqualified" || participant.status === "disconnected") return;
    participant.falseStartReferenceProgress ??= participant.trackProgress;
    let delta = participant.trackProgress - participant.falseStartReferenceProgress;
    if (delta > .5) delta -= 1;
    else if (delta < -.5) delta += 1;
    const forwardProgress = Math.max(0, delta);
    if (participant.speedKph < 5 && forwardProgress < .0008) {
      participant.falseStartMovementStartedAt = null;
      return;
    }
    participant.falseStartMovementStartedAt ??= now.toISOString();
    if (forwardProgress < .002 && now.getTime() - Date.parse(participant.falseStartMovementStartedAt) < 250) return;
    participant.falseStartPenalized = true;
    const penalty: PenaltySnapshot = {
      id: crypto.randomUUID(), participantId: participant.id, kind: "time", valueSeconds: 5, gridPlaces: null,
      reason: "抢跑：五盏红灯熄灭前车辆已经移动", issuedAt: now.toISOString(), isServed: false, isRevoked: false
    };
    this.state.penalties.push(penalty);
    this.state.banner = this.newBanner("penalty", `抢跑 · ${participant.displayName}`, "自动加罚 5 秒", participant.id, 8_000, now);
    this.touch();
  }

  private eligibleForFinalQualifyingLap(participant: ParticipantState): boolean {
    return participant.isConnected && !participant.isInPitLane && !participant.isInServiceZone &&
      participant.currentLapSeconds > .05 && participant.status !== "disqualified" &&
      participant.status !== "didNotFinish" && participant.status !== "disconnected";
  }

  private completeQualifyingIfReady(now: Date): void {
    if (this.state.phase !== "qualifying" || !this.state.qualifyingTimeExpired ||
        this.state.participants.some(participant => participant.qualifyingFinalLapPending)) return;
    this.state.phase = "grid";
    this.state.flag = "green";
    this.state.flagMessage = null;
    this.state.qualifyingEndsAt = null;
    this.state.qualifyingTimeExpired = false;
    for (const participant of this.state.participants.filter(candidate => candidate.isConnected))
      participant.status = "ready";
    this.state.banner ??= this.newBanner("chequeredFlag", "排位赛结束", "成绩已冻结", null, 8_000, now);
  }

  private clearYellowState(): void {
    this.state.manualFullCourseYellow = null;
    this.state.manualSectorYellows = {};
    for (const participant of this.state.participants) {
      participant.automaticYellowActive = false;
      participant.hazardCandidateStartedAt = null;
      participant.hazardRecoveryStartedAt = null;
    }
  }

  private prepareRace(): void {
    this.clearYellowState();
    this.state.startsAt = null;
    this.state.startSequenceAt = null;
    this.state.illuminatedStartLights = 0;
    this.state.startLightsOut = false;
    this.state.qualifyingEndsAt = null;
    this.state.qualifyingTimeExpired = false;
    this.state.banner = null;
    this.state.receivedLapEvents = [];
    for (const participant of this.state.participants) this.resetParticipant(participant, true);
  }

  private resetCompetitiveState(): void {
    this.clearYellowState();
    this.state.penalties = [];
    this.state.receivedLapEvents = [];
    this.state.startsAt = null;
    this.state.startSequenceAt = null;
    this.state.illuminatedStartLights = 0;
    this.state.startLightsOut = false;
    this.state.qualifyingEndsAt = null;
    this.state.qualifyingTimeExpired = false;
    this.state.banner = null;
    for (const participant of this.state.participants) this.resetParticipant(participant, false);
  }

  private resetParticipant(participant: ParticipantState, onTrack: boolean): void {
    participant.isReady = false;
    participant.completedLaps = 0;
    participant.currentSector = 0;
    participant.trackProgress = 0;
    participant.currentLapSeconds = 0;
    participant.lastLapSeconds = null;
    participant.bestLapSeconds = null;
    participant.bestSectorSeconds = [];
    participant.finishedAt = null;
    participant.isInPitLane = false;
    participant.isInServiceZone = false;
    participant.pitServiceElapsedSeconds = 0;
    participant.pitServiceRequirementMet = false;
    participant.completedPitServices = 0;
    participant.automaticYellowActive = false;
    participant.hazardCandidateStartedAt = null;
    participant.hazardRecoveryStartedAt = null;
    participant.qualifyingFinalLapPending = false;
    participant.falseStartArmedAt = null;
    participant.falseStartReferenceProgress = null;
    participant.falseStartMovementStartedAt = null;
    participant.falseStartPenalized = false;
    participant.status = participant.isConnected ? (onTrack ? "onTrack" : "connected") : "disconnected";
  }

  private updateBestSectors(participant: ParticipantState, sectors: number[]): void {
    if (!Array.isArray(sectors)) return;
    for (let index = 0; index < Math.min(20, sectors.length); index++) {
      const value = sectors[index];
      if (!Number.isFinite(value) || value <= 0 || value > 7_200) continue;
      while (participant.bestSectorSeconds.length <= index) participant.bestSectorSeconds.push(null);
      const current = participant.bestSectorSeconds[index];
      participant.bestSectorSeconds[index] = current === null || current === undefined ? value : Math.min(current, value);
    }
  }

  private fastestLap(): { participant: ParticipantState; time: number } | null {
    const participant = this.state.participants
      .filter(candidate => candidate.bestLapSeconds !== null && candidate.bestLapSeconds !== undefined)
      .sort((left, right) => (left.bestLapSeconds! - right.bestLapSeconds!) ||
        left.joinedAt.localeCompare(right.joinedAt))[0];
    return participant ? { participant, time: participant.bestLapSeconds! } : null;
  }

  private fastestSectors(): Array<number | null> {
    const count = this.state.participants.reduce(
      (maximum, participant) => Math.max(maximum, participant.bestSectorSeconds.length), 0);
    return Array.from({ length: count }, (_, index) => {
      const values = this.state.participants
        .map(participant => participant.bestSectorSeconds[index])
        .filter((value): value is number => typeof value === "number" && value > 0);
      return values.length === 0 ? null : Math.min(...values);
    });
  }

  private orderParticipants(): ParticipantState[] {
    const participants = [...this.state.participants];
    if (this.state.phase === "qualifying" || this.state.phase === "grid") {
      return participants.sort((left, right) => {
        if (left.bestLapSeconds === null || left.bestLapSeconds === undefined)
          return right.bestLapSeconds === null || right.bestLapSeconds === undefined
            ? left.joinedAt.localeCompare(right.joinedAt) : 1;
        if (right.bestLapSeconds === null || right.bestLapSeconds === undefined) return -1;
        return left.bestLapSeconds - right.bestLapSeconds || left.joinedAt.localeCompare(right.joinedAt);
      });
    }
    if (["outLap", "formationLap", "race", "countdown", "suspended", "finished"].includes(this.state.phase)) {
      return participants.sort((left, right) =>
        terminalRank(left.status) - terminalRank(right.status) ||
        compareNullableText(left.status === "finished" ? left.finishedAt : null,
          right.status === "finished" ? right.finishedAt : null) ||
        right.completedLaps - left.completedLaps ||
        right.trackProgress - left.trackProgress ||
        left.joinedAt.localeCompare(right.joinedAt));
    }
    return participants.sort((left, right) => Number(right.isReady) - Number(left.isReady) ||
      left.joinedAt.localeCompare(right.joinedAt));
  }

  private newBanner(
    kind: BannerKind,
    title: string,
    detail: string | null,
    participantId: string | null,
    durationMilliseconds: number | null,
    now: Date): BannerSnapshot {
    return {
      id: crypto.randomUUID(),
      kind,
      title,
      detail,
      participantId,
      createdAt: now.toISOString(),
      expiresAt: durationMilliseconds === null ? null : new Date(now.getTime() + durationMilliseconds).toISOString()
    };
  }
}

function accepted(): CommandResult { return { ok: true }; }
function rejected(error: string): CommandResult { return { ok: false, error }; }
function terminal(status: ParticipantStatus): boolean {
  return status === "finished" || status === "didNotFinish" || status === "disqualified";
}
function terminalRank(status: ParticipantStatus): number {
  if (status === "finished") return 0;
  if (status === "didNotFinish") return 2;
  if (status === "disqualified") return 3;
  if (status === "disconnected") return 4;
  return 1;
}
function compareNullableText(left?: string | null, right?: string | null): number {
  if (!left) return right ? 1 : 0;
  if (!right) return -1;
  return left.localeCompare(right);
}
function equalsIgnoreCase(left: string, right?: string | null): boolean {
  return typeof right === "string" && left.toUpperCase() === right.toUpperCase();
}
function constantTimeTextEquals(left: string, right: string): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++)
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  return difference === 0;
}
function createResumeToken(): string {
  const bytes = new Uint8Array(24);
  crypto.getRandomValues(bytes);
  return [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
}
function calculateIlluminatedStartLights(now: Date, sequenceAt: Date): number {
  if (now.getTime() < sequenceAt.getTime()) return 0;
  return clampInteger(Math.floor((now.getTime() - sequenceAt.getTime()) / 1_000) + 1, 1, 5);
}
function randomInteger(minimum: number, maximum: number): number {
  const values = new Uint32Array(1);
  crypto.getRandomValues(values);
  return minimum + values[0] % (maximum - minimum + 1);
}
function formatLap(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${(seconds % 60).toFixed(3).padStart(6, "0")}`;
}
function penaltyDescription(penalty: PenaltySnapshot): string {
  if (penalty.kind === "warning") return `警告 · ${penalty.reason}`;
  if (penalty.kind === "time") return `加罚 ${penalty.valueSeconds?.toFixed(1)} 秒 · ${penalty.reason}`;
  if (penalty.kind === "driveThrough") return `通过维修区处罚 · ${penalty.reason}`;
  if (penalty.kind === "stopAndGo") return `停车 ${penalty.valueSeconds?.toFixed(1)} 秒 · ${penalty.reason}`;
  if (penalty.kind === "gridDrop") return `退后 ${penalty.gridPlaces} 个发车位 · ${penalty.reason}`;
  return `取消比赛资格 · ${penalty.reason}`;
}
