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
  type TeamDefinition,
  type TrackLimitMode,
  type RaceEventSnapshot,
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
  teamCount?: number;
  driversPerTeam?: number;
  teams?: TeamDefinition[];
  trackName?: string | null;
  trackLimitMode?: TrackLimitMode;
}

interface ParticipantState {
  id: string;
  resumeToken: string;
  displayName: string;
  themeColor: string;
  teamName?: string | null;
  teamId?: string | null;
  teamColor?: string | null;
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
  lastLapCompletedAt?: string | null;
  raceTotalSeconds?: number | null;
  trackToleranceMeters?: number;
  trackLimitWarnings?: number;
  trackLimitExcursionStartedAt?: string | null;
  trackLimitRejoinStartedAt?: string | null;
  trackLimitMaximumOffsetMeters?: number;
  trackLimitSeverePenaltyIssued?: boolean;
  trackLimitStartProgress?: number;
  trackLimitTravelDistanceMeters?: number;
  trackLimitLastMonotonicMilliseconds?: number;
  lapHasTrackLimitIncident?: boolean;
  bestSectorSeconds: Array<number | null>;
  bestLapSectorSeconds?: Array<number | null>;
  isInPitLane: boolean;
  isInServiceZone: boolean;
  pitServiceElapsedSeconds: number;
  pitServiceRequirementMet: boolean;
  completedPitServices: number;
  pitLaneElapsedSeconds?: number;
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
  progressContinuityReady?: boolean;
  lastTelemetryMonotonicMilliseconds?: number;
  lastContinuityProgress?: number;
  shortcutPenaltyIssued?: boolean;
  pitSpeedCandidateStartedAt?: string | null;
  pitSpeedPenaltyIssued?: boolean;
  penaltyServiceActive?: boolean;
  penaltyServiceAttempted?: boolean;
  penaltyServiceElapsedSeconds?: number;
  penaltyServiceRequiredSeconds?: number;
  penaltyServiceLastUpdatedAt?: string | null;
  penaltyServiceCompletedAt?: string | null;
  driveThroughVisitActive?: boolean;
  driveThroughLineCrossings?: number;
  driveThroughReminderAt?: string | null;
  driveThroughOverdue?: boolean;
  driveThroughStopCandidateStartedAt?: string | null;
  pitVisitHadServiceStop?: boolean;
  pitVisitPaused?: boolean;
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
  raceSuspendedAt?: string | null;
  raceSuspendedMilliseconds?: number;
  raceEndedAt?: string | null;
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
  driversPerTeam: number;
  teams: TeamDefinition[];
  chequeredImminent: boolean;
  trackName?: string | null;
  trackId?: string | null;
  trackRevision?: string | null;
  trackPackageHash?: string | null;
  manualFullCourseYellow?: string | null;
  manualSectorYellows: Record<string, string>;
  trackLimitMode: TrackLimitMode;
  events: RaceEventSnapshot[];
  eventSequence: number;
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
const fallbackTeamColors = [
  "#42D7E8", "#FF4057", "#5A8CFF", "#FFD328", "#B86CFF", "#34D17B",
  "#FF8A3D", "#EE4FA6", "#B8F34A", "#8FA3B8", "#6FD6A7", "#F28B82"
];

function normalizeTeams(requestedCount: number, configured?: TeamDefinition[] | null): TeamDefinition[] {
  const count = clampInteger(requestedCount, 1, 12), source = Array.isArray(configured) ? configured : [];
  const ids = new Set<string>(), names = new Set<string>(), result: TeamDefinition[] = [];
  for (let index = 0; index < count; index++) {
    const candidate = source[index];
    let id = cleanText(candidate?.id, 40) ?? `team-${index + 1}`;
    if (ids.has(id.toLowerCase())) id = `team-${index + 1}`;
    while (ids.has(id.toLowerCase())) id += "-next";
    ids.add(id.toLowerCase());
    let name = cleanText(candidate?.name, 24) ?? `车队 ${index + 1}`;
    if (names.has(name.toLowerCase())) name = `${name} ${index + 1}`;
    while (names.has(name.toLowerCase())) name += "-";
    names.add(name.toLowerCase());
    const themeColor = isThemeColor(candidate?.themeColor)
      ? candidate.themeColor.toUpperCase() : fallbackTeamColors[index % fallbackTeamColors.length];
    result.push({ id, name, themeColor });
  }
  return result;
}

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
      raceSuspendedAt: null,
      raceSuspendedMilliseconds: 0,
      raceEndedAt: null,
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
      ,driversPerTeam: clampInteger(configuration.driversPerTeam ?? 6, 1, 12)
      ,teams: normalizeTeams(configuration.teamCount ?? configuration.teams?.length ?? 2, configuration.teams)
      ,chequeredImminent: false
      ,trackName: cleanText(configuration.trackName, 128)
      ,trackId: cleanText(configuration.trackId, 128)
      ,trackRevision: cleanText(configuration.trackRevision, 64)
      ,trackPackageHash: cleanText(configuration.trackPackageHash, 128)
      ,trackLimitMode: configuration.trackLimitMode ?? "warningsOnly"
      ,events: []
      ,eventSequence: 0
    };
  }

  serialize(): StoredRaceState {
    return structuredClone(this.state);
  }

  events(limit = 250): RaceEventSnapshot[] {
    return this.state.events.slice(-clampInteger(limit, 1, 500)).reverse().map(event => ({ ...event }));
  }

  login(request: LoginRequest, now = new Date()): LoginResult {
    const displayName = cleanText(request.displayName, 20);
    const resumeToken = cleanText(request.resumeToken, 256);
    const resumed = resumeToken
      ? this.state.participants.find(participant => constantTimeTextEquals(participant.resumeToken, resumeToken))
      : undefined;
    let team = this.state.allowTeams ? this.resolveTeam(request.teamId, request.teamName) : null;
    if (this.state.allowTeams && !team && isLegacyTeamClient(request.clientVersion))
      team = this.selectLegacyTeam(resumed?.id);
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
    if (this.state.allowTeams && !team)
      return { ok: false, code: "teamRequired", message: "请选择服务端已经配置的车队。" };

    if (resumed) {
      if (this.hasDuplicateName(displayName, resumed.id))
        return { ok: false, code: "duplicateName", message: "该比赛显示名已被其他车手使用。" };
      if (team && !this.teamHasCapacity(team.id, resumed.id))
        return { ok: false, code: "teamFull", message: `${team.name} 已达到每队 ${this.state.driversPerTeam} 人上限。` };
      resumed.displayName = displayName;
      resumed.themeColor = request.themeColor.toUpperCase();
      resumed.teamName = team?.name ?? null;
      resumed.teamId = team?.id ?? null;
      resumed.teamColor = team?.themeColor ?? null;
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
    if (team && !this.teamHasCapacity(team.id))
      return { ok: false, code: "teamFull", message: `${team.name} 已达到每队 ${this.state.driversPerTeam} 人上限。` };

    const participant: ParticipantState = {
      id: crypto.randomUUID(),
      resumeToken: createResumeToken(),
      displayName,
      themeColor: request.themeColor.toUpperCase(),
      teamName: team?.name ?? null,
      teamId: team?.id ?? null,
      teamColor: team?.themeColor ?? null,
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
      lastLapCompletedAt: null,
      raceTotalSeconds: null,
      trackToleranceMeters: 18,
      trackLimitWarnings: 0,
      trackLimitExcursionStartedAt: null,
      trackLimitRejoinStartedAt: null,
      trackLimitMaximumOffsetMeters: 0,
      trackLimitSeverePenaltyIssued: false,
      trackLimitStartProgress: 0,
      trackLimitTravelDistanceMeters: 0,
      trackLimitLastMonotonicMilliseconds: 0,
      lapHasTrackLimitIncident: false,
      bestSectorSeconds: [],
      bestLapSectorSeconds: [],
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
      ,penaltyServiceActive: false
      ,penaltyServiceAttempted: false
      ,penaltyServiceElapsedSeconds: 0
      ,penaltyServiceRequiredSeconds: 0
      ,penaltyServiceLastUpdatedAt: null
      ,penaltyServiceCompletedAt: null
      ,driveThroughVisitActive: false
      ,driveThroughLineCrossings: 0
      ,driveThroughReminderAt: null
      ,driveThroughOverdue: false
      ,driveThroughStopCandidateStartedAt: null
      ,pitVisitHadServiceStop: false
      ,pitVisitPaused: false
    };
    this.state.participants.push(participant);
    this.recordEvent("participantJoined", `${participant.displayName} 进入房间。`, participant.id, now);
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
    this.recordEvent("participantDisconnected", `${participant.displayName} 离开房间。`, participant.id, now);
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
    this.recordEvent("readyChanged", `${participant.displayName}${participant.isReady ? "已准备" : "取消准备"}。`, participant.id, now);
    this.touch();
    return accepted();
  }

  updateTelemetry(participantId: string, update: TelemetryUpdate, now = new Date()): CommandResult {
    const participant = this.find(participantId);
    if (!participant) return rejected("车手不存在。");
    participant.isConnected = true;
    participant.lastSeenAt = now.toISOString();
    const wasInPitLane = participant.isInPitLane;
    const wasInServiceZone = participant.isInServiceZone;
    const priorServices = participant.completedPitServices;
    this.updatePenaltyServiceState(participant, update, now);
    this.updatePitServiceState(participant, update);
    if (!wasInPitLane && participant.isInPitLane)
      this.recordEvent("pitEntered", `${participant.displayName} 进入维修区。`, participant.id, now);
    if (!wasInServiceZone && participant.isInServiceZone)
      this.recordEvent("pitBoxEntered", `${participant.displayName} 停入换胎区。`, participant.id, now);
    if (priorServices < participant.completedPitServices)
      this.recordEvent("pitServiceCompleted", `${participant.displayName} 完成换胎停留。`, participant.id, now);
    if (wasInPitLane && !participant.isInPitLane)
      this.recordEvent("pitExited", `${participant.displayName} 离开维修区。`, participant.id, now);
    if (!update.isTelemetryValid || update.isPausedOrRewinding) {
      participant.progressContinuityReady = false;
      return accepted();
    }

    this.evaluateShortcut(participant, update, now);
    participant.trackProgress = clamp(update.trackProgress, 0, 1);
    participant.lateralOffsetMeters = clamp(update.lateralOffsetMeters, -1_000, 1_000);
    participant.mapX = clamp(update.mapX, -10_000_000, 10_000_000);
    participant.mapY = clamp(update.mapY, -10_000_000, 10_000_000);
    participant.speedKph = clamp(update.speedKph, 0, 800);
    participant.currentSector = clampInteger(update.currentSector, 0, this.state.sectorCount - 1);
    participant.currentLapSeconds = clamp(update.currentLapSeconds, 0, 7_200);
    participant.trackToleranceMeters = update.trackToleranceMeters && update.trackToleranceMeters > 0
      ? clamp(update.trackToleranceMeters, 4, 50)
      : 18;
    participant.gripCondition = allowedGrip.has(update.gripCondition) ? update.gripCondition : "unknown";
    if (!terminal(participant.status)) {
      participant.status = participant.isInServiceZone
        ? "inService"
        : participant.isInPitLane ? "inPitLane" : "onTrack";
    }
    this.evaluateFalseStart(participant, now);
    this.evaluateTrackLimits(participant, update, now);
    this.evaluatePitSpeeding(participant, update, now);
    const yellowBefore = participant.automaticYellowActive ?? false;
    this.evaluateAutomaticYellow(participant, now);
    if (!yellowBefore && participant.automaticYellowActive)
      this.recordEvent("automaticYellow", `${participant.displayName} 触发第 ${(participant.automaticYellowSector ?? 0) + 1} 分段自动黄旗：${participant.automaticYellowReason ?? "异常车辆"}。`, participant.id, now);
    else if (yellowBefore && !participant.automaticYellowActive)
      this.recordEvent("automaticYellowCleared", `${participant.displayName} 的异常状态已恢复，自动黄旗解除。`, participant.id, now);
    this.refreshYellowFlag(now);
    this.refreshChequeredImminent(now);
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
      this.recordEvent("lapInvalid", `${participant.displayName} 的本圈无效：${cleanText(completed.invalidReason, 120) ?? "客户端判定无效"}。`, participant.id, now);
      if (this.state.phase === "race")
        this.updateDriveThroughDeadline(participant, now, false);
      participant.qualifyingFinalLapPending = false;
      this.completeQualifyingIfReady(now);
      this.touch();
      return accepted();
    }
    if (!Number.isFinite(completed.lapSeconds) || completed.lapSeconds < 3 || completed.lapSeconds > 21_600)
      return rejected("圈速数值超出有效范围。");

    const priorFastest = this.fastestLap();
    const bestLapEligible = completed.isBestLapEligible !== false && !participant.lapHasTrackLimitIncident;
    const improvesPersonalBest = bestLapEligible &&
      (participant.bestLapSeconds === null || participant.bestLapSeconds === undefined ||
       completed.lapSeconds < participant.bestLapSeconds - .0005);
    participant.completedLaps++;
    participant.lastLapSeconds = completed.lapSeconds;
    participant.lastLapCompletedAt = now.toISOString();
    if (improvesPersonalBest) {
      participant.bestLapSeconds = completed.lapSeconds;
      participant.bestLapSectorSeconds = this.sanitizeLapSectors(completed.sectorSeconds);
    }
    participant.currentLapSeconds = 0;
    participant.currentSector = 0;
    participant.shortcutPenaltyIssued = false;
    participant.progressContinuityReady = false;
    participant.lastSeenAt = now.toISOString();
    if (bestLapEligible) this.updateBestSectors(participant, completed.sectorSeconds);
    participant.lapHasTrackLimitIncident = false;
    participant.qualifyingFinalLapPending = false;
    this.recordEvent(
      bestLapEligible ? "lapCompleted" : "lapCompletedNotFastest",
      `${participant.displayName} 完成第 ${participant.completedLaps} 圈：${formatLap(completed.lapSeconds)}${bestLapEligible ? "" : "（不计最快圈）"}。`,
      participant.id,
      now);

    const newFastest = this.fastestLap();
    if (newFastest && (!priorFastest || newFastest.time < priorFastest.time - 0.0005)) {
      this.state.banner = this.newBanner(
        "fastestLap", "全场最快圈", `${newFastest.participant.displayName} · ${formatLap(newFastest.time)}`,
        newFastest.participant.id, 8_000, now);
    }
    if (this.state.phase === "race") {
      this.updateDriveThroughDeadline(
        participant,
        now,
        participant.completedLaps >= this.state.totalRaceLaps || this.state.flag === "chequered");
      if (this.state.flag === "chequered" && !terminal(participant.status)) {
        participant.status = "finished";
        participant.finishedAt = now.toISOString();
        participant.raceTotalSeconds ??= this.raceElapsedSeconds(now);
      } else if (this.state.flag !== "chequered" &&
                 participant.completedLaps >= this.state.totalRaceLaps) {
        participant.status = "finished";
        participant.finishedAt = now.toISOString();
        participant.raceTotalSeconds ??= this.raceElapsedSeconds(now);
        this.state.flag = "chequered";
        this.state.chequeredImminent = false;
        this.state.flagMessage = "领跑者已完成预定圈数";
        this.clearYellowState();
        this.state.banner = this.newBanner(
          "chequeredFlag", "方格旗", `${participant.displayName} 率先完成 ${this.state.totalRaceLaps} 圈`,
          participant.id, 8_000, now);
        this.recordEvent("chequeredFlag", `${participant.displayName} 率先完成预定圈数，方格旗生效。`, participant.id, now);
      }
      const classified = this.state.participants.filter(candidate => candidate.isConnected &&
        candidate.status !== "didNotFinish" && candidate.status !== "disqualified" && candidate.status !== "disconnected");
      if (this.state.flag === "chequered" && classified.every(candidate => candidate.status === "finished")) {
        this.state.phase = "finished";
        this.state.raceEndedAt = now.toISOString();
        const winner = this.orderParticipants(now).find(candidate => candidate.status === "finished");
        if (winner) this.state.banner = this.newBanner(
          "winner", "比赛胜者",
          `${winner.displayName}  ${formatRaceTime(this.adjustedRaceTotalSeconds(winner, now))}`,
          winner.id, null, now);
      }
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
      ,teamCount: this.state.teams.length
      ,driversPerTeam: this.state.driversPerTeam
      ,teams: this.state.teams.map(team => ({ ...team }))
      ,trackLimitMode: this.state.trackLimitMode
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
    const allowTeams = command.allowTeams !== false;
    const teamCount = clampInteger(command.teamCount ?? command.teams?.length ?? 2, 1, 12);
    if (allowTeams) {
      if (!Array.isArray(command.teams) || command.teams.length !== teamCount)
        return rejected(`已开启车队，请完整配置 ${teamCount} 支车队的名称和代表色。`);
      const ids = new Set<string>(), names = new Set<string>();
      for (const team of command.teams) {
        const id = cleanText(team.id, 40), name = cleanText(team.name, 24);
        if (!id || !name) return rejected("每支车队都需要有效的名称和标识。");
        if (ids.has(id.toLowerCase()) || names.has(name.toLowerCase()))
          return rejected("车队名称和标识不能重复。");
        if (!isThemeColor(team.themeColor)) return rejected(`${name} 的代表色不是有效的 #RRGGBB 颜色。`);
        ids.add(id.toLowerCase()); names.add(name.toLowerCase());
      }
    }
    this.state.sessionName = sessionName;
    this.state.totalRaceLaps = clampInteger(command.totalRaceLaps, 1, 999);
    this.state.sectorCount = clampInteger(command.sectorCount, 1, 20);
    this.state.automaticYellowEnabled = Boolean(command.automaticYellowEnabled);
    this.state.slowSpeedKph = clamp(command.slowSpeedKph, 3, 50);
    this.state.slowDurationSeconds = clamp(command.slowDurationSeconds, 1, 15);
    this.state.severeLateralOffsetMeters = clamp(command.severeLateralOffsetMeters, 5, 200);
    this.state.recoveryDurationSeconds = clamp(command.recoveryDurationSeconds, 1, 15);
    this.state.allowTeams = allowTeams;
    this.state.driversPerTeam = clampInteger(command.driversPerTeam ?? 6, 1, 12);
    this.state.teams = normalizeTeams(teamCount, command.teams);
    this.state.trackName = trackName;
    this.state.trackId = trackId;
    this.state.trackRevision = cleanText(command.trackRevision, 64);
    this.state.trackPackageHash = trackHash;
    this.state.trackLimitMode = command.trackLimitMode === "automatic" || command.trackLimitMode === "disabled"
      ? command.trackLimitMode : "warningsOnly";
    for (const participant of this.state.participants) {
      const selectedTeam = this.state.allowTeams ? this.resolveTeam(participant.teamId, participant.teamName) : null;
      participant.teamId = selectedTeam?.id ?? null;
      participant.teamName = selectedTeam?.name ?? null;
      participant.teamColor = selectedTeam?.themeColor ?? null;
    }
    if (!this.state.automaticYellowEnabled) {
      for (const participant of this.state.participants) {
        participant.automaticYellowActive = false;
        participant.hazardCandidateStartedAt = null;
        participant.hazardRecoveryStartedAt = null;
      }
      this.refreshYellowFlag(now);
    }
    this.touch();
    this.recordEvent("roomSettings", `房间设置已更新，赛道边界处理为 ${this.state.trackLimitMode}。`, null, now);
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
        this.state.raceSuspendedAt = null;
        this.state.raceSuspendedMilliseconds = 0;
        this.state.raceEndedAt = null;
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
    this.recordEvent("sessionChanged", `赛事阶段切换为 ${command.phase}。`, null, now);
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
        if (this.state.phaseBeforeSuspension === "race" && this.state.raceSuspendedAt)
          this.state.raceSuspendedMilliseconds = (this.state.raceSuspendedMilliseconds ?? 0) +
            Math.max(0, now.getTime() - Date.parse(this.state.raceSuspendedAt));
        this.state.raceSuspendedAt = null;
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
      if (this.state.phase === "race" && !this.state.raceSuspendedAt)
        this.state.raceSuspendedAt = now.toISOString();
      this.state.phase = "suspended";
      this.state.flag = "red";
      this.state.flagMessage = message ?? "比赛暂停";
    }
    this.recordEvent("flagChanged", requestedSector === null
      ? `总控发布 ${command.flag}。`
      : `总控对第 ${requestedSector + 1} 分段发布 ${command.flag}。`, null, now);
    this.touch();
    return accepted();
  }

  applyPenalty(command: PenaltyCommand, now = new Date()): CommandResult {
    const participant = this.find(command.participantId);
    if (!participant) return rejected("车手不存在。");
    if (!allowedPenalties.has(command.kind)) return rejected("处罚类型无效。");
    const reason = cleanText(command.reason, 160);
    if (!reason) return rejected("处罚原因不能为空。");
    const penalty: PenaltySnapshot = command.kind === "driveThrough"
      ? this.createDriveThroughPenalty(participant, reason, now)
      : {
        id: crypto.randomUUID(),
        participantId: participant.id,
        kind: command.kind,
        valueSeconds: command.kind === "time"
          ? clampInteger(command.valueSeconds ?? 5, 1, 6)
          : command.kind === "stopAndGo" ? clamp(command.valueSeconds, 1, 3_600) : null,
        gridPlaces: command.kind === "gridDrop" ? clampInteger(command.gridPlaces, 1, 99) : null,
        reason,
        issuedAt: now.toISOString(),
        isServed: false,
        isRevoked: false,
        isPostRaceAdjustment: false
      };
    this.state.penalties.push(penalty);
    if (command.kind === "disqualification") participant.status = "disqualified";
    this.state.banner = this.newBanner(
      "penalty", `处罚 · ${participant.displayName}`, penaltyDescription(penalty), participant.id, 10_000, now);
    this.recordEvent("manualPenalty", `${participant.displayName}：${penaltyDescription(penalty)}；${reason}。`, participant.id, now);
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
    this.recordEvent("participantStatus", `${participant.displayName} 被标记为 ${command.status}：${cleanText(command.reason, 160) ?? "未填写原因"}。`, participant.id, now);
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
        this.recordEvent("raceStarted", "五盏红灯熄灭，正赛开始。", null, now);
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
      this.recordEvent("qualifyingExpired", `排位赛计时结束，${pendingCount} 名车手仍可完成最后飞驰圈。`, null, now);
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
    const ordered = this.orderParticipants(now);
    const leader = ordered[0];
    let prior: ParticipantState | undefined;
    const participants: ParticipantSnapshot[] = ordered.map((participant, index) => {
      const participantPenalties = this.state.penalties.filter(penalty =>
        penalty.participantId === participant.id && !penalty.isRevoked);
      const timePenaltySeconds = this.timePenaltySeconds(participant.id);
      const pendingTimePenaltySeconds = this.pendingTimePenaltySeconds(participant.id);
      const hasPendingDriveThrough = this.hasPendingDriveThrough(participant.id);
      const raceTotalSeconds = this.isRaceClassificationPhase()
        ? participant.raceTotalSeconds ?? this.raceElapsedSeconds(now)
        : null;
      const adjustedRaceTotalSeconds = raceTotalSeconds === null ? null : raceTotalSeconds +
        (participant.status === "finished" ? timePenaltySeconds : 0);
      const qualifying = this.state.phase === "qualifying" || this.state.phase === "grid";
      const gapToLeaderSeconds = qualifying
        ? participant.bestLapSeconds !== null && participant.bestLapSeconds !== undefined &&
          leader?.bestLapSeconds !== null && leader?.bestLapSeconds !== undefined
          ? participant.bestLapSeconds - leader.bestLapSeconds : null
        : leader ? this.raceDeltaSeconds(leader, participant, now) : null;
      const intervalSeconds = qualifying
        ? participant.bestLapSeconds !== null && participant.bestLapSeconds !== undefined &&
          prior?.bestLapSeconds !== null && prior?.bestLapSeconds !== undefined
          ? participant.bestLapSeconds - prior.bestLapSeconds : null
        : prior ? this.raceDeltaSeconds(prior, participant, now) : null;
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
        gapToLeaderSeconds,
        intervalSeconds,
        isInPitLane: participant.isInPitLane,
        isInServiceZone: participant.isInServiceZone,
        pitServiceElapsedSeconds: participant.pitServiceElapsedSeconds,
        pitServiceRequirementMet: participant.pitServiceRequirementMet,
        completedPitServices: participant.completedPitServices,
        gripCondition: participant.gripCondition,
        bestSectorSeconds: [...participant.bestSectorSeconds],
        penalties: participantPenalties,
        lastSeenAt: participant.lastSeenAt,
        qualifyingFinalLapPending: participant.qualifyingFinalLapPending ?? false,
        raceTotalSeconds,
        adjustedRaceTotalSeconds,
        timePenaltySeconds,
        trackLimitWarnings: participant.trackLimitWarnings ?? 0,
        teamId: participant.teamId,
        teamColor: participant.teamColor,
        pitLaneElapsedSeconds: participant.pitLaneElapsedSeconds ?? 0,
        pendingTimePenaltySeconds,
        isServingTimePenalty: participant.penaltyServiceActive ?? false,
        penaltyServiceElapsedSeconds: participant.penaltyServiceElapsedSeconds ?? 0,
        penaltyServiceRequiredSeconds: participant.penaltyServiceRequiredSeconds ?? 0,
        hasPendingDriveThrough,
        penaltyServiceCompleted: Boolean(participant.penaltyServiceCompletedAt &&
          now.getTime() - Date.parse(participant.penaltyServiceCompletedAt) <= 3_000),
        driveThroughLapsRemaining: hasPendingDriveThrough
          ? Math.max(0, 2 - (participant.driveThroughLineCrossings ?? 0)) : null,
        driveThroughReminderAt: participant.driveThroughReminderAt ?? null,
        driveThroughOverdue: participant.driveThroughOverdue ?? false,
        isServingDriveThrough: Boolean(participant.driveThroughVisitActive && participant.isInPitLane)
      };
      prior = participant;
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
      fastestLapSectorSeconds: [...(fastest?.participant.bestLapSectorSeconds ?? [])],
      banner: this.state.banner?.expiresAt && Date.parse(this.state.banner.expiresAt) <= now.getTime()
        ? null : this.state.banner,
      participants,
      serverTime: now.toISOString(),
      yellowZones: this.yellowZones(),
      sectorCount: this.state.sectorCount,
      allowTeams: this.state.allowTeams,
      trackName: this.state.trackName,
      blueFlags: this.blueFlags(),
      raceElapsedSeconds: this.isRaceClassificationPhase() ? this.raceElapsedSeconds(now) : null,
      suspendedFromPhase: this.state.phase === "suspended" ? this.state.phaseBeforeSuspension : null,
      driversPerTeam: this.state.driversPerTeam,
      teams: this.state.teams.map(team => ({ ...team })),
      chequeredImminent: this.state.chequeredImminent
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
      raceSuspendedAt: stored.raceSuspendedAt ?? null,
      raceSuspendedMilliseconds: clamp(stored.raceSuspendedMilliseconds ?? 0, 0, Number.MAX_SAFE_INTEGER),
      raceEndedAt: stored.raceEndedAt ?? null,
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
        falseStartPenalized: participant.falseStartPenalized ?? false,
        lastLapCompletedAt: participant.lastLapCompletedAt ?? null,
        raceTotalSeconds: participant.raceTotalSeconds ?? null,
        trackToleranceMeters: clamp(participant.trackToleranceMeters ?? 18, 4, 50),
        trackLimitWarnings: clampInteger(participant.trackLimitWarnings ?? 0, 0, 999),
        trackLimitExcursionStartedAt: participant.trackLimitExcursionStartedAt ?? null,
        trackLimitRejoinStartedAt: participant.trackLimitRejoinStartedAt ?? null,
        trackLimitMaximumOffsetMeters: clamp(participant.trackLimitMaximumOffsetMeters ?? 0, 0, 1_000),
        trackLimitSeverePenaltyIssued: participant.trackLimitSeverePenaltyIssued ?? false,
        trackLimitStartProgress: clamp(participant.trackLimitStartProgress ?? 0, 0, 1),
        trackLimitTravelDistanceMeters: clamp(participant.trackLimitTravelDistanceMeters ?? 0, 0, 1_000_000),
        trackLimitLastMonotonicMilliseconds: clamp(participant.trackLimitLastMonotonicMilliseconds ?? 0, 0, Number.MAX_SAFE_INTEGER),
        lapHasTrackLimitIncident: participant.lapHasTrackLimitIncident ?? false,
        bestLapSectorSeconds: Array.isArray(participant.bestLapSectorSeconds)
          ? participant.bestLapSectorSeconds.slice(0, 20) : [],
        penaltyServiceActive: participant.penaltyServiceActive ?? false,
        penaltyServiceAttempted: participant.penaltyServiceAttempted ?? false,
        penaltyServiceElapsedSeconds: clamp(participant.penaltyServiceElapsedSeconds ?? 0, 0, 3_600),
        penaltyServiceRequiredSeconds: clamp(participant.penaltyServiceRequiredSeconds ?? 0, 0, 3_600),
        penaltyServiceLastUpdatedAt: participant.penaltyServiceLastUpdatedAt ?? null,
        penaltyServiceCompletedAt: participant.penaltyServiceCompletedAt ?? null,
        driveThroughVisitActive: participant.driveThroughVisitActive ?? false,
        driveThroughLineCrossings: clampInteger(participant.driveThroughLineCrossings ?? 0, 0, 99),
        driveThroughReminderAt: participant.driveThroughReminderAt ?? null,
        driveThroughOverdue: participant.driveThroughOverdue ?? false,
        driveThroughStopCandidateStartedAt: participant.driveThroughStopCandidateStartedAt ?? null,
        pitVisitHadServiceStop: participant.pitVisitHadServiceStop ?? false,
        pitVisitPaused: participant.pitVisitPaused ?? false,
        teamId: cleanText(participant.teamId, 40),
        teamColor: isThemeColor(participant.teamColor) ? participant.teamColor.toUpperCase() : null
      })) : [],
      penalties: Array.isArray(stored.penalties)
        ? stored.penalties.map(penalty => ({
          ...penalty,
          isPostRaceAdjustment: penalty.isPostRaceAdjustment ?? false
        }))
        : [],
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
      ,driversPerTeam: clampInteger(stored.driversPerTeam ?? 6, 1, 12)
      ,teams: normalizeTeams(stored.teams?.length ?? 2, stored.teams)
      ,chequeredImminent: stored.chequeredImminent ?? false
      ,trackName: cleanText(stored.trackName, 128)
      ,trackId: cleanText(stored.trackId, 128)
      ,trackRevision: cleanText(stored.trackRevision, 64)
      ,trackPackageHash: cleanText(stored.trackPackageHash, 128)
      ,trackLimitMode: stored.trackLimitMode === "automatic" || stored.trackLimitMode === "disabled"
        ? stored.trackLimitMode : "warningsOnly"
      ,events: Array.isArray(stored.events) ? stored.events.slice(-500) : []
      ,eventSequence: clampInteger(stored.eventSequence ?? 0, 0, Number.MAX_SAFE_INTEGER)
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

  private resolveTeam(requestedId?: string | null, requestedName?: string | null): TeamDefinition | null {
    const id = cleanText(requestedId, 40);
    if (id) {
      const byId = this.state.teams.find(team => equalsIgnoreCase(team.id, id));
      if (byId) return byId;
    }
    const name = cleanText(requestedName, 24);
    return name ? this.state.teams.find(team => equalsIgnoreCase(team.name, name)) ?? null : null;
  }

  private teamHasCapacity(teamId: string, exceptId?: string): boolean {
    return this.state.participants.filter(participant =>
      participant.id !== exceptId && equalsIgnoreCase(teamId, participant.teamId)).length < this.state.driversPerTeam;
  }

  private selectLegacyTeam(exceptId?: string): TeamDefinition | null {
    const candidates = this.state.teams
      .map((team, index) => ({
        team,
        index,
        members: this.state.participants.filter(participant =>
          participant.id !== exceptId && equalsIgnoreCase(team.id, participant.teamId)).length
      }))
      .filter(candidate => candidate.members < this.state.driversPerTeam)
      .sort((left, right) => left.members - right.members || left.index - right.index);
    return candidates[0]?.team ?? null;
  }

  private refreshChequeredImminent(now: Date): void {
    if (this.state.phase !== "race" || this.state.flag === "chequered") {
      this.state.chequeredImminent = false;
      return;
    }
    if (this.state.chequeredImminent) return;
    const leader = this.orderParticipants(now).find(participant =>
      participant.isConnected && !terminal(participant.status) && participant.status !== "disconnected");
    if (leader && leader.completedLaps === this.state.totalRaceLaps - 1 && leader.trackProgress >= .94)
      this.state.chequeredImminent = true;
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
    this.recordEvent("falseStart", `${participant.displayName} 抢跑，记录 5 秒待执行罚时。`, participant.id, now);
    this.touch();
  }

  private updatePitServiceState(participant: ParticipantState, update: TelemetryUpdate): void {
    participant.isInPitLane = Boolean(update.isInPitLane);
    participant.isInServiceZone = participant.isInPitLane && Boolean(update.isInServiceZone);
    const serviceBlocked = this.pendingTimePenaltySeconds(participant.id) > 0 ||
      Boolean(participant.penaltyServiceActive);
    participant.pitServiceElapsedSeconds = participant.isInServiceZone && !serviceBlocked
      ? clamp(update.pitServiceElapsedSeconds, 0, 60)
      : 0;
    participant.pitLaneElapsedSeconds = clamp(update.pitLaneElapsedSeconds ?? 0, 0, 86_400);
    participant.pitServiceRequirementMet = participant.isInServiceZone && !serviceBlocked &&
      Boolean(update.pitServiceRequirementMet);
    const reportedServices = clampInteger(update.completedPitServices, 0, 999);
    if (participant.pitServiceRequirementMet && reportedServices === participant.completedPitServices + 1)
      participant.completedPitServices++;
  }

  private updatePenaltyServiceState(participant: ParticipantState, update: TelemetryUpdate, now: Date): void {
    const enteredPit = !participant.isInPitLane && Boolean(update.isInPitLane);
    const exitedPit = participant.isInPitLane && !update.isInPitLane;
    const leftServiceZone = participant.isInServiceZone && !update.isInServiceZone;
    if (enteredPit) {
      participant.pitVisitHadServiceStop = false;
      participant.pitVisitPaused = false;
      participant.driveThroughVisitActive = this.hasPendingDriveThrough(participant.id);
      participant.driveThroughStopCandidateStartedAt = null;
    }
    if (update.isInPitLane && update.isPausedOrRewinding) participant.pitVisitPaused = true;
    if (participant.driveThroughVisitActive && update.isInPitLane) {
      if (clamp(update.speedKph, 0, 800) <= 1) {
        participant.driveThroughStopCandidateStartedAt ??= now.toISOString();
        if (now.getTime() - Date.parse(participant.driveThroughStopCandidateStartedAt) >= 1_000)
          participant.pitVisitHadServiceStop = true;
      } else participant.driveThroughStopCandidateStartedAt = null;
    }

    const pendingTime = this.pendingTimePenaltySeconds(participant.id);
    if (pendingTime > 0 && update.isInServiceZone) {
      if (update.isPausedOrRewinding || !update.isTelemetryValid) {
        if (participant.penaltyServiceActive || participant.penaltyServiceAttempted)
          this.convertTimePenaltyToDriveThrough(participant, now, "执行罚时期间打开暂停菜单或使用回转");
      } else if (clamp(update.speedKph, 0, 800) <= 1) {
        if (!participant.penaltyServiceActive) {
          participant.penaltyServiceActive = true;
          participant.penaltyServiceAttempted = true;
          participant.penaltyServiceElapsedSeconds = 0;
          participant.penaltyServiceRequiredSeconds = pendingTime;
          participant.penaltyServiceLastUpdatedAt = now.toISOString();
          this.recordEvent("penaltyServiceStarted", `${participant.displayName} 开始执行 ${pendingTime.toFixed(0)} 秒停车罚时。`, participant.id, now);
        } else if (participant.penaltyServiceLastUpdatedAt) {
          const elapsed = Math.max(0, (now.getTime() - Date.parse(participant.penaltyServiceLastUpdatedAt)) / 1_000);
          participant.penaltyServiceElapsedSeconds = Math.min(
            participant.penaltyServiceRequiredSeconds ?? pendingTime,
            (participant.penaltyServiceElapsedSeconds ?? 0) + elapsed);
          participant.penaltyServiceLastUpdatedAt = now.toISOString();
        }
        if ((participant.penaltyServiceElapsedSeconds ?? 0) + .0005 >=
            (participant.penaltyServiceRequiredSeconds ?? pendingTime)) {
          this.markPendingPenaltiesServed(participant.id, "time");
          participant.penaltyServiceElapsedSeconds = participant.penaltyServiceRequiredSeconds ?? pendingTime;
          participant.penaltyServiceActive = false;
          participant.penaltyServiceAttempted = false;
          participant.penaltyServiceLastUpdatedAt = null;
          participant.penaltyServiceCompletedAt = now.toISOString();
          this.recordEvent("penaltyServiceCompleted", `${participant.displayName} 已完成停车罚时，可以开始换胎。`, participant.id, now);
        }
      } else if (participant.penaltyServiceActive && (participant.penaltyServiceElapsedSeconds ?? 0) > 0) {
        this.convertTimePenaltyToDriveThrough(participant, now, "停车罚时完成前车辆移动");
      }
    } else if (pendingTime > 0 && leftServiceZone && participant.penaltyServiceAttempted) {
      this.convertTimePenaltyToDriveThrough(participant, now, "停车罚时完成前离开换胎区");
    }

    if (exitedPit) {
      if (this.pendingTimePenaltySeconds(participant.id) > 0 && participant.penaltyServiceAttempted)
        this.convertTimePenaltyToDriveThrough(participant, now, "未完成停车罚时便离开维修区");
      if (participant.driveThroughVisitActive && !participant.pitVisitHadServiceStop &&
          !participant.pitVisitPaused && this.hasPendingDriveThrough(participant.id)) {
        this.markPendingPenaltiesServed(participant.id, "driveThrough");
        participant.penaltyServiceCompletedAt = now.toISOString();
        participant.driveThroughReminderAt = now.toISOString();
        participant.driveThroughLineCrossings = 0;
        participant.driveThroughOverdue = false;
        this.recordEvent("driveThroughServed", `${participant.displayName} 已完成通过维修区处罚。`, participant.id, now);
      } else if (participant.driveThroughVisitActive && this.hasPendingDriveThrough(participant.id)) {
        participant.driveThroughReminderAt = now.toISOString();
        this.recordEvent(
          "driveThroughAttemptFailed",
          participant.pitVisitPaused
            ? `${participant.displayName} 执行通过维修区处罚时暂停或回转，本次进站无效。`
            : `${participant.displayName} 执行通过维修区处罚时停车，本次进站无效。`,
          participant.id,
          now);
      }
      participant.driveThroughVisitActive = false;
      participant.driveThroughStopCandidateStartedAt = null;
      participant.pitVisitHadServiceStop = false;
      participant.pitVisitPaused = false;
    }
  }

  private evaluateShortcut(participant: ParticipantState, update: TelemetryUpdate, now: Date): void {
    const monotonic = Number.isFinite(update.clientMonotonicMilliseconds)
      ? update.clientMonotonicMilliseconds : 0;
    const trackLength = update.trackLengthMeters && update.trackLengthMeters > 0
      ? clamp(update.trackLengthMeters, 50, 100_000) : 0;
    if (participant.progressContinuityReady && trackLength >= 50 &&
        monotonic > (participant.lastTelemetryMonotonicMilliseconds ?? 0)) {
      const elapsedSeconds = (monotonic - (participant.lastTelemetryMonotonicMilliseconds ?? 0)) / 1_000;
      let progressDelta = clamp(update.trackProgress, 0, 1) - (participant.lastContinuityProgress ?? 0);
      if (progressDelta < -.75) progressDelta += 1;
      const routeDistance = progressDelta * trackLength;
      const reportedSpeed = Math.max(participant.speedKph, clamp(update.speedKph, 0, 800)) / 3.6;
      const plausibleDistance = Math.max(60, reportedSpeed * elapsedSeconds * 3 + 30);
      if ((this.state.phase === "race" || this.state.phase === "qualifying") &&
          !participant.isInPitLane && !participant.isInServiceZone && !update.isApproachingPit &&
          !terminal(participant.status) && participant.status !== "disconnected" &&
          elapsedSeconds > 0 && elapsedSeconds <= 2 && progressDelta > 0 && progressDelta < .75 &&
          routeDistance > plausibleDistance && !participant.shortcutPenaltyIssued) {
        participant.shortcutPenaltyIssued = true;
        participant.lapHasTrackLimitIncident = true;
        participant.trackLimitSeverePenaltyIssued = true;
        this.registerTrackLimitIncident(
          participant, true,
          `跨越约 ${routeDistance.toFixed(0)} 米参考路线，确认获得距离优势`, now);
      }
    }
    participant.lastTelemetryMonotonicMilliseconds = monotonic;
    participant.lastContinuityProgress = clamp(update.trackProgress, 0, 1);
    participant.progressContinuityReady = monotonic > 0;
  }

  private evaluatePitSpeeding(participant: ParticipantState, update: TelemetryUpdate, now: Date): void {
    const limit = update.pitSpeedLimitKph && update.pitSpeedLimitKph > 0
      ? clamp(update.pitSpeedLimitKph, 10, 300) : 0;
    if (this.state.phase !== "race" || !update.isInPitLane || limit <= 0 ||
        terminal(participant.status) || participant.status === "disconnected") {
      participant.pitSpeedCandidateStartedAt = null;
      if (!update.isInPitLane) participant.pitSpeedPenaltyIssued = false;
      return;
    }
    if (update.speedKph <= limit + 2) {
      participant.pitSpeedCandidateStartedAt = null;
      return;
    }
    if (participant.pitSpeedPenaltyIssued) return;
    participant.pitSpeedCandidateStartedAt ??= now.toISOString();
    if (now.getTime() - Date.parse(participant.pitSpeedCandidateStartedAt) < 400) return;
    participant.pitSpeedPenaltyIssued = true;
    this.addAutomaticTrackLimitPenalty(
      participant, "time", 5,
      `维修区超速：${update.speedKph.toFixed(0)} km/h，限速 ${limit.toFixed(0)} km/h`, now);
  }

  private evaluateTrackLimits(participant: ParticipantState, update: TelemetryUpdate, now: Date): void {
    if ((this.state.phase !== "race" && this.state.phase !== "qualifying") ||
        participant.isInPitLane || participant.isInServiceZone || update.isApproachingPit ||
        terminal(participant.status) || participant.status === "disconnected") {
      this.resetTrackLimitExcursion(participant);
      return;
    }
    const minorOffsetMeters = clamp(participant.trackToleranceMeters ?? 18, 6, 30);
    const severeOffsetMeters = Math.max(minorOffsetMeters + 6, this.state.severeLateralOffsetMeters);
    const absoluteOffset = Math.abs(participant.lateralOffsetMeters);
    if (absoluteOffset >= minorOffsetMeters) {
      participant.lapHasTrackLimitIncident = true;
      participant.trackLimitRejoinStartedAt = null;
      if (!participant.trackLimitExcursionStartedAt) {
        participant.trackLimitExcursionStartedAt = now.toISOString();
        participant.trackLimitStartProgress = participant.trackProgress;
        participant.trackLimitTravelDistanceMeters = 0;
        participant.trackLimitLastMonotonicMilliseconds = update.clientMonotonicMilliseconds;
      } else if (update.clientMonotonicMilliseconds > (participant.trackLimitLastMonotonicMilliseconds ?? 0)) {
        const elapsed = Math.min(2,
          (update.clientMonotonicMilliseconds - (participant.trackLimitLastMonotonicMilliseconds ?? 0)) / 1_000);
        participant.trackLimitTravelDistanceMeters = (participant.trackLimitTravelDistanceMeters ?? 0) +
          Math.max(0, participant.speedKph) / 3.6 * elapsed;
        participant.trackLimitLastMonotonicMilliseconds = update.clientMonotonicMilliseconds;
      }
      participant.trackLimitMaximumOffsetMeters = Math.max(
        participant.trackLimitMaximumOffsetMeters ?? 0, absoluteOffset);
      return;
    }
    if (!participant.trackLimitExcursionStartedAt) return;
    const rejoinOffsetMeters = Math.max(3, minorOffsetMeters - 4);
    if (absoluteOffset > rejoinOffsetMeters) {
      participant.trackLimitRejoinStartedAt = null;
      return;
    }
    participant.trackLimitRejoinStartedAt ??= now.toISOString();
    if (now.getTime() - Date.parse(participant.trackLimitRejoinStartedAt) < 400) return;

    const excursionDuration = now.getTime() - Date.parse(participant.trackLimitExcursionStartedAt);
    const maximumOffset = participant.trackLimitMaximumOffsetMeters ?? 0;
    let routeDelta = participant.trackProgress - (participant.trackLimitStartProgress ?? 0);
    if (routeDelta < -.5) routeDelta += 1;
    else if (routeDelta > .75) routeDelta -= 1;
    const trackLength = update.trackLengthMeters && update.trackLengthMeters >= 50
      ? clamp(update.trackLengthMeters, 50, 100_000) : 0;
    const routeDistance = Math.max(0, routeDelta) * trackLength;
    const gainedDistance = Math.max(0, routeDistance - (participant.trackLimitTravelDistanceMeters ?? 0));
    const wasHandled = participant.trackLimitSeverePenaltyIssued ?? false;
    this.resetTrackLimitExcursion(participant);
    if (wasHandled || excursionDuration < 250) return;
    const minimumGain = Math.max(6, minorOffsetMeters * .35);
    if (gainedDistance < minimumGain) return;
    const severe = maximumOffset >= severeOffsetMeters && gainedDistance >= Math.max(12, minorOffsetMeters) ||
      gainedDistance >= Math.max(35, severeOffsetMeters);
    this.registerTrackLimitIncident(participant, severe,
      `偏离参考路线 ${maximumOffset.toFixed(1)} 米，估算获得约 ${gainedDistance.toFixed(1)} 米距离优势`, now);
  }

  private registerTrackLimitIncident(
    participant: ParticipantState,
    severe: boolean,
    evidence: string,
    now: Date): void {
    if (this.state.trackLimitMode === "disabled") return;
    participant.trackLimitWarnings = (participant.trackLimitWarnings ?? 0) + 1;
    if (this.state.trackLimitMode === "warningsOnly") {
      this.addAutomaticTrackLimitPenalty(participant, "warning", null,
        `疑似切弯获利：${evidence}（事件 ${participant.trackLimitWarnings}，待总控核查）`, now);
      return;
    }
    if (severe) {
      this.addAutomaticTrackLimitPenalty(participant, "time", 5, `严重切弯：${evidence}`, now);
      return;
    }
    if (participant.trackLimitWarnings <= 3) {
      this.addAutomaticTrackLimitPenalty(participant, "warning", null,
        `轻微切弯获利：${evidence}（警告 ${participant.trackLimitWarnings}/3）`, now);
      return;
    }
    participant.trackLimitWarnings = 0;
    this.addAutomaticTrackLimitPenalty(participant, "time", 5,
      `轻微切弯警告累计超过 3 次：${evidence}`, now);
  }

  private addAutomaticTrackLimitPenalty(
    participant: ParticipantState,
    kind: PenaltyKind,
    valueSeconds: number | null,
    reason: string,
    now: Date): void {
    const penalty: PenaltySnapshot = {
      id: crypto.randomUUID(), participantId: participant.id, kind, valueSeconds, gridPlaces: null,
      reason, issuedAt: now.toISOString(), isServed: false, isRevoked: false
    };
    this.state.penalties.push(penalty);
    this.state.banner = this.newBanner(
      "penalty", kind === "warning" ? `赛道边界警告 · ${participant.displayName}` : `自动判罚 · ${participant.displayName}`,
      penaltyDescription(penalty), participant.id, 8_000, now);
    this.recordEvent(kind === "warning" ? "trackLimitWarning" : "automaticPenalty",
      `${participant.displayName}：${penaltyDescription(penalty)}；${reason}。`, participant.id, now);
    this.touch();
  }

  private resetTrackLimitExcursion(participant: ParticipantState): void {
    participant.trackLimitExcursionStartedAt = null;
    participant.trackLimitRejoinStartedAt = null;
    participant.trackLimitMaximumOffsetMeters = 0;
    participant.trackLimitSeverePenaltyIssued = false;
    participant.trackLimitStartProgress = 0;
    participant.trackLimitTravelDistanceMeters = 0;
    participant.trackLimitLastMonotonicMilliseconds = 0;
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
    this.state.chequeredImminent = false;
    this.state.startsAt = null;
    this.state.startSequenceAt = null;
    this.state.raceSuspendedAt = null;
    this.state.raceSuspendedMilliseconds = 0;
    this.state.raceEndedAt = null;
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
    this.state.chequeredImminent = false;
    this.state.penalties = [];
    this.state.receivedLapEvents = [];
    this.state.startsAt = null;
    this.state.startSequenceAt = null;
    this.state.raceSuspendedAt = null;
    this.state.raceSuspendedMilliseconds = 0;
    this.state.raceEndedAt = null;
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
    participant.lastLapCompletedAt = null;
    participant.raceTotalSeconds = null;
    participant.trackToleranceMeters = 18;
    participant.trackLimitWarnings = 0;
    this.resetTrackLimitExcursion(participant);
    participant.lapHasTrackLimitIncident = false;
    participant.bestSectorSeconds = [];
    participant.bestLapSectorSeconds = [];
    participant.finishedAt = null;
    participant.isInPitLane = false;
    participant.isInServiceZone = false;
    participant.pitServiceElapsedSeconds = 0;
    participant.pitServiceRequirementMet = false;
    participant.completedPitServices = 0;
    participant.pitLaneElapsedSeconds = 0;
    participant.automaticYellowActive = false;
    participant.hazardCandidateStartedAt = null;
    participant.hazardRecoveryStartedAt = null;
    participant.qualifyingFinalLapPending = false;
    participant.falseStartArmedAt = null;
    participant.falseStartReferenceProgress = null;
    participant.falseStartMovementStartedAt = null;
    participant.falseStartPenalized = false;
    participant.progressContinuityReady = false;
    participant.lastTelemetryMonotonicMilliseconds = 0;
    participant.lastContinuityProgress = 0;
    participant.shortcutPenaltyIssued = false;
    participant.pitSpeedCandidateStartedAt = null;
    participant.pitSpeedPenaltyIssued = false;
    participant.penaltyServiceActive = false;
    participant.penaltyServiceAttempted = false;
    participant.penaltyServiceElapsedSeconds = 0;
    participant.penaltyServiceRequiredSeconds = 0;
    participant.penaltyServiceLastUpdatedAt = null;
    participant.penaltyServiceCompletedAt = null;
    participant.driveThroughVisitActive = false;
    participant.driveThroughLineCrossings = 0;
    participant.driveThroughReminderAt = null;
    participant.driveThroughOverdue = false;
    participant.driveThroughStopCandidateStartedAt = null;
    participant.pitVisitHadServiceStop = false;
    participant.pitVisitPaused = false;
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

  private sanitizeLapSectors(sectors: number[]): Array<number | null> {
    if (!Array.isArray(sectors)) return [];
    return sectors.slice(0, 20).map(value =>
      Number.isFinite(value) && value > 0 && value <= 7_200 ? value : null);
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

  private raceElapsedSeconds(now: Date): number {
    if (!this.state.startsAt) return 0;
    const endedAt = this.state.raceEndedAt ? Date.parse(this.state.raceEndedAt) : now.getTime();
    let suspendedMilliseconds = this.state.raceSuspendedMilliseconds ?? 0;
    if (this.state.raceSuspendedAt)
      suspendedMilliseconds += Math.max(0, endedAt - Date.parse(this.state.raceSuspendedAt));
    return Math.max(0, (endedAt - Date.parse(this.state.startsAt) - suspendedMilliseconds) / 1_000);
  }

  private timePenaltySeconds(participantId: string): number {
    return this.state.penalties
      .filter(penalty => penalty.participantId === participantId && !penalty.isRevoked &&
        !penalty.isServed && penalty.kind === "time")
      .reduce((total, penalty) => total + (penalty.valueSeconds ?? 0), 0);
  }

  private pendingTimePenaltySeconds(participantId: string): number {
    return this.state.penalties
      .filter(penalty => penalty.participantId === participantId && !penalty.isRevoked &&
        !penalty.isServed && penalty.kind === "time" && !penalty.isPostRaceAdjustment)
      .reduce((total, penalty) => total + (penalty.valueSeconds ?? 0), 0);
  }

  private hasPendingDriveThrough(participantId: string): boolean {
    return this.state.penalties.some(penalty => penalty.participantId === participantId &&
      !penalty.isRevoked && !penalty.isServed && penalty.kind === "driveThrough");
  }

  private markPendingPenaltiesServed(participantId: string, kind: PenaltyKind): void {
    for (const penalty of this.state.penalties) {
      if (penalty.participantId === participantId && penalty.kind === kind &&
          !penalty.isRevoked && !penalty.isServed &&
          !(kind === "time" && penalty.isPostRaceAdjustment)) penalty.isServed = true;
    }
  }

  private createDriveThroughPenalty(
    participant: ParticipantState,
    reason: string,
    now: Date): PenaltySnapshot {
    if (this.state.phase === "race" && this.state.totalRaceLaps - participant.completedLaps <= 3) {
      return {
        id: crypto.randomUUID(), participantId: participant.id, kind: "time",
        valueSeconds: 20, gridPlaces: null,
        reason: `最后三圈下发的通过维修区处罚：${reason}`,
        issuedAt: now.toISOString(), isServed: false, isRevoked: false,
        isPostRaceAdjustment: true
      };
    }
    participant.driveThroughLineCrossings = 0;
    participant.driveThroughReminderAt = now.toISOString();
    participant.driveThroughOverdue = false;
    participant.penaltyServiceCompletedAt = null;
    return {
      id: crypto.randomUUID(), participantId: participant.id, kind: "driveThrough",
      valueSeconds: null, gridPlaces: null, reason,
      issuedAt: now.toISOString(), isServed: false, isRevoked: false,
      isPostRaceAdjustment: false
    };
  }

  private updateDriveThroughDeadline(
    participant: ParticipantState,
    now: Date,
    raceFinishedForParticipant: boolean): void {
    if (!this.hasPendingDriveThrough(participant.id) ||
        participant.driveThroughVisitActive || participant.isInPitLane) return;
    participant.driveThroughLineCrossings = (participant.driveThroughLineCrossings ?? 0) + 1;
    participant.driveThroughReminderAt = now.toISOString();
    const remaining = Math.max(0, 2 - participant.driveThroughLineCrossings);
    if (!raceFinishedForParticipant && participant.driveThroughLineCrossings <= 2) {
      this.recordEvent(
        "driveThroughReminder",
        remaining > 0
          ? `${participant.displayName} 的通过维修区处罚还可跨越终点线 ${remaining} 次。`
          : `${participant.displayName} 必须在本圈结束前进入维修区执行通过维修区处罚。`,
        participant.id,
        now);
      return;
    }
    this.convertDriveThroughToTimeAdjustment(
      participant,
      now,
      raceFinishedForParticipant
        ? "比赛已结束，无法继续执行通过维修区处罚"
        : "收到处罚后第三次从赛道上跨越终点线");
  }

  private convertDriveThroughToTimeAdjustment(
    participant: ParticipantState,
    now: Date,
    reason: string): void {
    if (!this.hasPendingDriveThrough(participant.id)) return;
    this.markPendingPenaltiesServed(participant.id, "driveThrough");
    this.state.penalties.push({
      id: crypto.randomUUID(), participantId: participant.id, kind: "time",
      valueSeconds: 20, gridPlaces: null,
      reason: `通过维修区处罚未按期执行：${reason}`,
      issuedAt: now.toISOString(), isServed: false, isRevoked: false,
      isPostRaceAdjustment: true
    });
    participant.driveThroughOverdue = true;
    participant.driveThroughReminderAt = now.toISOString();
    participant.driveThroughVisitActive = false;
    participant.driveThroughStopCandidateStartedAt = null;
    this.state.banner = this.newBanner(
      "penalty", `通过维修区处罚逾期 · ${participant.displayName}`,
      "原处罚已替换为 20 秒完赛加时", participant.id, 8_000, now);
    this.recordEvent(
      "driveThroughOverdue",
      `${participant.displayName} 未按期执行通过维修区处罚，原处罚已替换为 20 秒完赛加时。`,
      participant.id,
      now);
  }

  private convertTimePenaltyToDriveThrough(participant: ParticipantState, now: Date, reason: string): void {
    const seconds = this.pendingTimePenaltySeconds(participant.id);
    if (seconds <= 0) return;
    this.markPendingPenaltiesServed(participant.id, "time");
    let replacement: PenaltySnapshot | null = null;
    if (!this.hasPendingDriveThrough(participant.id)) {
      replacement = this.createDriveThroughPenalty(participant, `停车罚时执行失败：${reason}`, now);
      this.state.penalties.push(replacement);
    }
    participant.penaltyServiceActive = false;
    participant.penaltyServiceAttempted = false;
    participant.penaltyServiceElapsedSeconds = 0;
    participant.penaltyServiceRequiredSeconds = 0;
    participant.penaltyServiceLastUpdatedAt = null;
    participant.penaltyServiceCompletedAt = null;
    this.state.banner = this.newBanner("penalty", `罚时执行失败 · ${participant.displayName}`,
      replacement?.isPostRaceAdjustment
        ? "比赛已进入最后三圈，已替换为 20 秒完赛加时"
        : "已转为通过维修区处罚", participant.id, 8_000, now);
    this.recordEvent("penaltyServiceFailed",
      replacement?.isPostRaceAdjustment
        ? `${participant.displayName} 未正确执行 ${seconds.toFixed(0)} 秒停车罚时；比赛已进入最后三圈，处罚替换为 20 秒完赛加时。`
        : `${participant.displayName} 未正确执行 ${seconds.toFixed(0)} 秒停车罚时，处罚已转为通过维修区。`,
      participant.id, now);
  }

  private adjustedRaceTotalSeconds(participant: ParticipantState, now: Date): number {
    return (participant.raceTotalSeconds ?? this.raceElapsedSeconds(now)) +
      (participant.status === "finished" ? this.timePenaltySeconds(participant.id) : 0);
  }

  private raceDeltaSeconds(reference: ParticipantState, participant: ParticipantState, now: Date): number | null {
    if (reference.id === participant.id) return 0;
    if (!this.isRaceClassificationPhase() || reference.completedLaps !== participant.completedLaps)
      return null;
    if (reference.status === "finished" && participant.status === "finished")
      return this.adjustedRaceTotalSeconds(participant, now) - this.adjustedRaceTotalSeconds(reference, now);
    if (reference.lastLapCompletedAt && participant.lastLapCompletedAt)
      return (Date.parse(participant.lastLapCompletedAt) - Date.parse(reference.lastLapCompletedAt)) / 1_000;
    return null;
  }

  private orderParticipants(now: Date): ParticipantState[] {
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
        right.completedLaps - left.completedLaps ||
        (left.status === "finished" && right.status === "finished"
          ? this.adjustedRaceTotalSeconds(left, now) - this.adjustedRaceTotalSeconds(right, now)
          : 0) ||
        right.trackProgress - left.trackProgress ||
        left.joinedAt.localeCompare(right.joinedAt));
    }
    return participants.sort((left, right) => Number(right.isReady) - Number(left.isReady) ||
      left.joinedAt.localeCompare(right.joinedAt));
  }

  private isRaceClassificationPhase(): boolean {
    return this.state.phase === "race" || this.state.phase === "finished" ||
      this.state.phase === "suspended" && this.state.phaseBeforeSuspension === "race";
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

  private recordEvent(type: string, message: string, participantId: string | null, now: Date): void {
    this.state.eventSequence = (this.state.eventSequence ?? 0) + 1;
    this.state.events.push({
      sequence: this.state.eventSequence,
      occurredAt: now.toISOString(),
      type,
      message,
      participantId
    });
    if (this.state.events.length > 500) this.state.events.splice(0, this.state.events.length - 500);
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
function equalsIgnoreCase(left: string, right?: string | null): boolean {
  return typeof right === "string" && left.toUpperCase() === right.toUpperCase();
}
function isLegacyTeamClient(clientVersion?: string | null): boolean {
  const match = /^v?(\d+)\.(\d+)\.(\d+)/i.exec(clientVersion?.trim() ?? "");
  return Boolean(match && Number(match[1]) === 1 && Number(match[2]) === 4 && Number(match[3]) <= 2);
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
function formatRaceTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "—";
  const totalMilliseconds = Math.round(seconds * 1_000);
  const hours = Math.floor(totalMilliseconds / 3_600_000);
  const minutes = Math.floor(totalMilliseconds % 3_600_000 / 60_000);
  const wholeSeconds = Math.floor(totalMilliseconds % 60_000 / 1_000);
  const milliseconds = totalMilliseconds % 1_000;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(wholeSeconds).padStart(2, "0")}.${String(milliseconds).padStart(3, "0")}`
    : `${minutes}:${String(wholeSeconds).padStart(2, "0")}.${String(milliseconds).padStart(3, "0")}`;
}
function penaltyDescription(penalty: PenaltySnapshot): string {
  if (penalty.kind === "warning") return `警告 · ${penalty.reason}`;
  if (penalty.kind === "time") return `加罚 ${penalty.valueSeconds?.toFixed(1)} 秒 · ${penalty.reason}`;
  if (penalty.kind === "driveThrough") return `通过维修区处罚 · ${penalty.reason}`;
  if (penalty.kind === "stopAndGo") return `停车 ${penalty.valueSeconds?.toFixed(1)} 秒 · ${penalty.reason}`;
  if (penalty.kind === "gridDrop") return `退后 ${penalty.gridPlaces} 个发车位 · ${penalty.reason}`;
  return `取消比赛资格 · ${penalty.reason}`;
}
