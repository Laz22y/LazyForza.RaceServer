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
  maximumObservers,
  type ParticipantCommand,
  type ParticipantSnapshot,
  type ParticipantStatus,
  type PenaltyCommand,
  type PenaltyUpdateCommand,
  type PenaltyKind,
  type PenaltySnapshot,
  type InvestigationCommand,
  type InvestigationSnapshot,
  type CollisionEvidenceSnapshot,
  type ReadyUpdate,
  type RoomSettings,
  type SessionCommand,
  type SessionPhase,
  type SessionSnapshot,
  type StageResultSnapshot,
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
  minimumRequiredPitStops?: number;
  trackId?: string | null;
  trackRevision?: string | null;
  trackPackageHash?: string | null;
  sectorCount?: number;
  automaticYellowEnabled?: boolean;
  automaticCollisionInvestigationsEnabled?: boolean;
  disconnectedLapRecoveryEnabled?: boolean;
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
  telemetryValid?: boolean;
  hasWorldPosition?: boolean;
  worldX?: number;
  worldY?: number;
  worldZ?: number;
  velocityX?: number;
  velocityY?: number;
  velocityZ?: number;
  lastTelemetryReceivedAt?: string | null;
  isApproachingPit?: boolean;
  isOnPitRoute?: boolean;
  lastReportedImpactSequence?: number;
  lastProcessedImpactSequence?: number;
  lastImpactAt?: string | null;
  lastImpactWorldX?: number;
  lastImpactWorldY?: number;
  lastImpactWorldZ?: number;
  lastImpactMagnitudeMps?: number;
  lastImpactSpeedLossMps?: number;
  lastImpactSmashableVelDiff?: number;
  lastImpactSmashableMass?: number;
  currentLapSeconds: number;
  lastLapSeconds?: number | null;
  bestLapSeconds?: number | null;
  lastLapCompletedAt?: string | null;
  disconnectedLapRecoveryUntil?: string | null;
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
  qualifyingEligible?: boolean;
  qualifyingEliminatedInSession?: number | null;
  qualifyingSessionBestLapSeconds?: Array<number | null>;
  practiceFinalLapPending?: boolean;
  practiceSessionBestLapSeconds?: Array<number | null>;
  falseStartArmedAt?: string | null;
  falseStartReferenceProgress?: number | null;
  falseStartMovementStartedAt?: string | null;
  falseStartPenalized?: boolean;
  progressContinuityReady?: boolean;
  lastTelemetryMonotonicMilliseconds?: number;
  lastContinuityProgress?: number;
  shortcutPenaltyIssued?: boolean;
  lastShortcutEvidenceId?: string | null;
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
  reservationActive?: boolean;
}

interface ObserverState {
  id: string;
  resumeToken: string;
  displayName: string;
  connectedAt: string;
}

interface RaceProgressSample {
  distanceLaps: number;
  elapsedSeconds: number;
}

interface RaceProgressTracker {
  lastProgress: number;
  lapOffset: number;
  ready: boolean;
}

interface CollisionPositionSample {
  at: number;
  worldX: number;
  worldY: number;
  worldZ: number;
  hasWorldVelocity: boolean;
  worldVelocityX: number;
  worldVelocityY: number;
  worldVelocityZ: number;
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
  qualifyingSessionNumber?: number;
  qualifyingSessionCount?: number;
  qualifyingSessionMinutes?: number[];
  qualifyingEliminationCounts?: number[];
  practiceEndsAt?: string | null;
  practiceTimeExpired?: boolean;
  practiceSessionNumber?: number;
  practiceSessionCount?: number;
  practiceSessionMinutes?: number[];
  banner?: BannerSnapshot | null;
  participants: ParticipantState[];
  observers: ObserverState[];
  penalties: PenaltySnapshot[];
  investigations?: InvestigationSnapshot[];
  receivedLapEvents: string[];
  sectorCount: number;
  automaticYellowEnabled: boolean;
  automaticCollisionInvestigationsEnabled: boolean;
  disconnectedLapRecoveryEnabled: boolean;
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
  minimumRequiredPitStops: number;
  events: RaceEventSnapshot[];
  eventSequence: number;
  revokedResumeTokens?: string[];
  activeResultStageId?: string | null;
  resultHistory?: StageResultSnapshot[];
}

export type CommandResult = { ok: true } | { ok: false; error: string };
export type LoginResult =
  | { ok: true; participantId: string; resumeToken: string; resumed: boolean; isObserver: boolean }
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

export function defaultQualifyingEliminations(participantCount: number, sessionCount: number): number[] {
  const total = clampInteger(participantCount, 0, 12);
  const count = clampInteger(sessionCount, 1, 3);
  if (count === 1 || total <= 1) return Array.from({ length: count - 1 }, () => 0);
  if (count === 2) return [Math.max(0, total - Math.max(1, Math.ceil(total / 2)))];
  const finalists = Math.max(2, Math.ceil(total / 2));
  const eliminated = Math.max(0, total - finalists);
  const q1 = Math.ceil(eliminated / 2);
  return [q1, eliminated - q1];
}

function compareOptionalTimes(left?: number | null, right?: number | null): number {
  const leftMissing = left === null || left === undefined;
  const rightMissing = right === null || right === undefined;
  if (leftMissing !== rightMissing) return leftMissing ? 1 : -1;
  if (leftMissing) return 0;
  return left! - right!;
}

export class RaceCore {
  private static readonly maximumLiveGapSamples = 3_600;
  private static readonly liveGapHistoryLaps = 1.25;
  private static readonly liveGapProgressJitter = .002;
  private static readonly maximumLiveGapDistanceLaps = .999;
  private static readonly collisionPairCooldownMilliseconds = 12_000;
  private static readonly collisionTrajectoryLifetimeMilliseconds = 2_000;
  private static readonly collisionTrajectoryMatchToleranceMilliseconds = 650;
  private static readonly collisionApproachLookbackMilliseconds = 280;
  private static readonly minimumCollisionImpactMagnitudeMps = 2.3;
  private static readonly strongCollisionImpactMagnitudeMps = 2.8;
  private static readonly minimumCollisionRelativeSpeedMps = 1.5;
  private static readonly minimumCollisionSpeedLossMps = 1.25;
  private static readonly minimumCollisionApproachMeters = .75;
  private static readonly maximumCollisionHorizontalDistanceMeters = 5.2;
  private static readonly maximumPairedImpactDistanceMeters = 6;
  private readonly maximumParticipants: number;
  private readonly liveProgressSamples = new Map<string, RaceProgressSample[]>();
  private readonly liveProgressTrackers = new Map<string, RaceProgressTracker>();
  private readonly collisionPairCooldowns = new Map<string, number>();
  private readonly collisionTrajectories = new Map<string, CollisionPositionSample[]>();
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
      minimumRequiredPitStops: clampInteger(configuration.minimumRequiredPitStops ?? 1, 0, 20),
      startsAt: null,
      startSequenceAt: null,
      raceSuspendedAt: null,
      raceSuspendedMilliseconds: 0,
      raceEndedAt: null,
      illuminatedStartLights: 0,
      startLightsOut: false,
      qualifyingEndsAt: null,
      qualifyingTimeExpired: false,
      qualifyingSessionNumber: 0,
      qualifyingSessionCount: 1,
      qualifyingSessionMinutes: [10],
      qualifyingEliminationCounts: [],
      practiceEndsAt: null,
      practiceTimeExpired: false,
      practiceSessionNumber: 0,
      practiceSessionCount: 1,
      practiceSessionMinutes: [60],
      banner: null,
      participants: [],
      observers: [],
      penalties: [],
      investigations: [],
      receivedLapEvents: []
      ,activeResultStageId: null
      ,resultHistory: []
      ,sectorCount: clampInteger(configuration.sectorCount ?? 3, 1, 20)
      ,automaticYellowEnabled: configuration.automaticYellowEnabled ?? true
      ,automaticCollisionInvestigationsEnabled: configuration.automaticCollisionInvestigationsEnabled ?? false
      ,disconnectedLapRecoveryEnabled: configuration.disconnectedLapRecoveryEnabled ?? false
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

  events(limit = 250, afterSequence?: number): RaceEventSnapshot[] {
    const selected = Number.isFinite(afterSequence)
      ? this.state.events.filter(event => event.sequence > (afterSequence as number))
      : this.state.events;
    return selected.slice(-clampInteger(limit, 1, 500)).reverse().map(event => ({ ...event }));
  }

  results(): StageResultSnapshot[] {
    return [...(this.state.resultHistory ?? [])].reverse().map(result => ({
      ...result,
      participants: result.participants.map(participant => ({
        ...participant,
        penalties: participant.penalties.map(penalty => ({ ...penalty }))
      }))
    }));
  }

  login(request: LoginRequest, now = new Date()): LoginResult {
    const displayName = cleanText(request.displayName, 20);
    const resumeToken = cleanText(request.resumeToken, 256);
    if (resumeToken && (this.state.revokedResumeTokens ?? []).some(token =>
      constantTimeTextEquals(token, resumeToken)))
      return {
        ok: false,
        code: "disconnectedByControl",
        message: "赛事总控已断开这个客户端。再次手动进入房间时可以重新申请席位。"
      };
    const isObserver = request.isObserver === true;
    const resumed = !isObserver && resumeToken
      ? this.state.participants.find(participant =>
          participant.reservationActive !== false && constantTimeTextEquals(participant.resumeToken, resumeToken))
      : undefined;
    const resumedObserver = isObserver && resumeToken
      ? this.state.observers.find(observer => constantTimeTextEquals(observer.resumeToken, resumeToken))
      : undefined;
    let team = !isObserver && this.state.allowTeams ? this.resolveTeam(request.teamId, request.teamName) : null;
    if (!isObserver && this.state.allowTeams && !team && isLegacyTeamClient(request.clientVersion))
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
    if (isObserver) {
      if (this.hasDuplicateName(displayName, resumedObserver?.id))
        return { ok: false, code: "duplicateName", message: "该显示名已被其他车手或 OB 使用。" };
      if (resumedObserver) {
        resumedObserver.displayName = displayName;
        this.recordEvent("observerResumed", `OB ${displayName} 重新连接。`, resumedObserver.id, now);
        this.touch();
        return {
          ok: true,
          participantId: resumedObserver.id,
          resumeToken: resumedObserver.resumeToken,
          resumed: true,
          isObserver: true
        };
      }
      if (this.state.observers.length >= maximumObservers)
        return { ok: false, code: "observerFull", message: `OB 席位已达到 ${maximumObservers} 人上限。` };
      const observer: ObserverState = {
        id: crypto.randomUUID(),
        resumeToken: createResumeToken(),
        displayName,
        connectedAt: now.toISOString()
      };
      this.state.observers.push(observer);
      this.recordEvent("observerJoined", `OB ${displayName} 加入转播席。`, observer.id, now);
      this.touch();
      return {
        ok: true,
        participantId: observer.id,
        resumeToken: observer.resumeToken,
        resumed: false,
        isObserver: true
      };
    }
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
      resumed.status = this.state.flag === "chequered" &&
                       (this.state.phase === "race" || this.state.phase === "finished") &&
                       !this.hasActiveDisconnectedLapRecovery(resumed, now)
        ? "didNotFinish"
        : ["race", "countdown", "practice", "outLap", "formationLap"].includes(this.state.phase)
          ? "onTrack"
          : resumed.isReady ? "ready" : "connected";
      if (resumed.status === "didNotFinish") resumed.finishedAt ??= now.toISOString();
      resumed.lastSeenAt = now.toISOString();
      this.tryCompleteRaceIfReady(now);
      this.touch();
      return { ok: true, participantId: resumed.id, resumeToken: resumed.resumeToken, resumed: true, isObserver: false };
    }

    if (this.state.phase === "qualifying" && (this.state.qualifyingSessionCount ?? 1) > 1)
      return { ok: false, code: "sessionLocked", message: "多节排位赛已经开始，只允许已参赛车手重新连接。" };

    if (this.state.participants.filter(participant => participant.reservationActive !== false).length >= this.maximumParticipants)
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
      disconnectedLapRecoveryUntil: null,
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
      lastShortcutEvidenceId: null,
      bestSectorSeconds: [],
      bestLapSectorSeconds: [],
      isInPitLane: false,
      isInServiceZone: false,
      pitServiceElapsedSeconds: 0,
      pitServiceRequirementMet: false,
      completedPitServices: 0,
      gripCondition: "unknown",
      qualifyingFinalLapPending: false,
      qualifyingEligible: true,
      qualifyingEliminatedInSession: null,
      qualifyingSessionBestLapSeconds: [null, null, null],
      practiceFinalLapPending: false,
      practiceSessionBestLapSeconds: [null, null, null],
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
      ,reservationActive: true
    };
    this.state.participants.push(participant);
    this.recordEvent("participantJoined", `${participant.displayName} 进入房间。`, participant.id, now);
    this.touch();
    return { ok: true, participantId: participant.id, resumeToken: participant.resumeToken, resumed: false, isObserver: false };
  }

  disconnect(participantId: string, now = new Date()): boolean {
    const participant = this.find(participantId);
    if (!participant) {
      const observerIndex = this.state.observers.findIndex(observer => observer.id === participantId);
      if (observerIndex < 0) return false;
      const [observer] = this.state.observers.splice(observerIndex, 1);
      this.recordEvent("observerDisconnected", `OB ${observer.displayName} 断开连接。`, observer.id, now);
      this.touch();
      return true;
    }
    if (!participant.isConnected) return false;
    participant.isConnected = false;
    if (!terminal(participant.status)) participant.status = "disconnected";
    participant.automaticYellowActive = false;
    participant.hazardCandidateStartedAt = null;
    participant.hazardRecoveryStartedAt = null;
    participant.lastSeenAt = now.toISOString();
    const effectivePhase = this.state.phase === "suspended"
      ? this.state.phaseBeforeSuspension
      : this.state.phase;
    const canRecoverLap = this.state.disconnectedLapRecoveryEnabled &&
      participant.status === "disconnected" &&
      (effectivePhase === "practice" || effectivePhase === "qualifying" || effectivePhase === "race");
    participant.disconnectedLapRecoveryUntil = canRecoverLap
      ? new Date(now.getTime() + 30_000).toISOString()
      : null;
    if (!canRecoverLap) {
      participant.qualifyingFinalLapPending = false;
      participant.practiceFinalLapPending = false;
    }
    this.recordEvent("participantDisconnected", `${participant.displayName} 离开房间。`, participant.id, now);
    this.completeQualifyingIfReady(now);
    this.completePracticeIfReady(now);
    this.refreshYellowFlag(now);
    this.tryCompleteRaceIfReady(now);
    this.touch();
    return true;
  }

  setReady(participantId: string, update: ReadyUpdate, now = new Date()): CommandResult {
    const participant = this.find(participantId);
    if (!participant) return rejected("车手不存在。");
    if (this.state.phase !== "lobby" && this.state.phase !== "practice" && this.state.phase !== "grid")
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
    if (this.canExecutePenalties(participant)) this.updatePenaltyServiceState(participant, update, now);
    else this.resetLivePenaltyServiceState(participant);
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
      const raceProgress = this.liveProgressTrackers.get(participant.id);
      if (raceProgress) raceProgress.ready = false;
      participant.telemetryValid = false;
      participant.lastReportedImpactSequence = Math.max(participant.lastReportedImpactSequence ?? 0, update.impactSequence ?? 0);
      participant.lastProcessedImpactSequence = participant.lastReportedImpactSequence;
      participant.lastImpactAt = null;
      participant.lastImpactMagnitudeMps = 0;
      participant.lastImpactSpeedLossMps = 0;
      participant.lastImpactSmashableVelDiff = 0;
      participant.lastImpactSmashableMass = 0;
      this.collisionTrajectories.delete(participant.id);
      return accepted();
    }

    this.evaluateShortcut(participant, update, now);
    participant.trackProgress = clamp(update.trackProgress, 0, 1);
    participant.lateralOffsetMeters = clamp(update.lateralOffsetMeters, -1_000, 1_000);
    participant.mapX = clamp(update.mapX, -10_000_000, 10_000_000);
    participant.mapY = clamp(update.mapY, -10_000_000, 10_000_000);
    participant.speedKph = clamp(update.speedKph, 0, 800);
    participant.telemetryValid = true;
    participant.hasWorldPosition = update.hasWorldPosition === true;
    participant.worldX = clamp(update.worldX, -10_000_000, 10_000_000);
    participant.worldY = clamp(update.worldY, -10_000_000, 10_000_000);
    participant.worldZ = clamp(update.worldZ, -10_000_000, 10_000_000);
    participant.velocityX = clamp(update.velocityX, -500, 500);
    participant.velocityY = clamp(update.velocityY, -500, 500);
    participant.velocityZ = clamp(update.velocityZ, -500, 500);
    participant.lastTelemetryReceivedAt = now.toISOString();
    this.recordCollisionPositionSample(participant, update, now);
    participant.isApproachingPit = update.isApproachingPit === true;
    participant.isOnPitRoute = update.isOnPitRoute === true;
    participant.currentSector = clampInteger(update.currentSector, 0, this.state.sectorCount - 1);
    participant.currentLapSeconds = clamp(update.currentLapSeconds, 0, 7_200);
    participant.trackToleranceMeters = update.trackToleranceMeters && update.trackToleranceMeters > 0
      ? clamp(update.trackToleranceMeters, 4, 50)
      : 18;
    participant.gripCondition = allowedGrip.has(update.gripCondition) ? update.gripCondition : "unknown";
    if (this.state.phase === "race")
      this.recordRaceProgressSample(participant, now,
        participant.isInPitLane || participant.isInServiceZone ||
        update.isApproachingPit === true || update.isOnPitRoute === true);
    if (!terminal(participant.status)) {
      participant.status = participant.isInServiceZone
        ? "inService"
        : participant.isInPitLane ? "inPitLane"
        : this.state.phase === "qualifying" && participant.qualifyingEligible === false ? "ready"
        : "onTrack";
    }
    this.evaluateFalseStart(participant, now);
    this.evaluateTrackLimits(participant, update, now);
    this.evaluatePitSpeeding(participant, update, now);
    const yellowBefore = participant.automaticYellowActive ?? false;
    this.evaluateAutomaticYellow(
      participant,
      now,
      Boolean(update.isOnPitRoute || update.isApproachingPit));
    if (!yellowBefore && participant.automaticYellowActive)
      this.recordEvent("automaticYellow", `${participant.displayName} 触发第 ${(participant.automaticYellowSector ?? 0) + 1} 分段自动黄旗：${participant.automaticYellowReason ?? "异常车辆"}。`, participant.id, now);
    else if (yellowBefore && !participant.automaticYellowActive)
      this.recordEvent("automaticYellowCleared", `${participant.displayName} 的异常状态已恢复，自动黄旗解除。`, participant.id, now);
    this.refreshYellowFlag(now);
    this.refreshChequeredImminent(now);
    this.evaluateCollisionInvestigation(participant, update, now);
    // completedLaps is deliberately ignored. Only a unique, valid lap event
    // may advance the server-authoritative counter.
    return accepted();
  }

  completeLap(participantId: string, completed: LapCompleted, now = new Date()): CommandResult {
    const participant = this.find(participantId);
    if (!participant) return rejected("车手不存在。");
    const eventId = cleanText(completed.eventId, 80);
    if (!eventId) return rejected("圈速事件编号无效。");
    if (this.state.receivedLapEvents.includes(eventId)) return accepted();
    if (completed.isRecoveredAfterDisconnect) {
      if (!this.state.disconnectedLapRecoveryEnabled)
        return rejected("服务端未开启断线圈速恢复。");
      if (!this.hasActiveDisconnectedLapRecovery(participant, now))
        return rejected("断线圈速恢复窗口已结束。");
    }
    if (terminal(participant.status) || participant.status === "disconnected")
      return rejected("该车手已经结束比赛，不能继续提交圈速。");
    if (this.state.phase !== "practice" && this.state.phase !== "qualifying" && this.state.phase !== "race")
      return rejected("当前阶段不接收圈速成绩。");
    if (this.state.phase === "qualifying" && participant.qualifyingEligible === false)
      return rejected("该车手已在本次排位赛中被淘汰。");
    if (this.state.phase === "qualifying" && this.state.qualifyingTimeExpired &&
        !participant.qualifyingFinalLapPending && !completed.isRecoveredAfterDisconnect)
      return rejected("排位赛计时已结束，该车手没有待完成的最后一圈。");
    if (this.state.phase === "practice" && this.state.practiceTimeExpired &&
        !participant.practiceFinalLapPending && !completed.isRecoveredAfterDisconnect)
      return rejected("练习赛计时已结束，该车手没有待完成的最后一圈。");
    if (completed.isValid &&
        (!Number.isFinite(completed.lapSeconds) || completed.lapSeconds < 3 || completed.lapSeconds > 21_600))
      return rejected("圈速数值超出有效范围。");
    this.state.receivedLapEvents.push(eventId);
    if (this.state.receivedLapEvents.length > 20_000)
      this.state.receivedLapEvents.splice(0, this.state.receivedLapEvents.length - 10_000);
    if (!completed.isValid) {
      participant.disconnectedLapRecoveryUntil = null;
      this.recordEvent("lapInvalid", `${participant.displayName} 的本圈无效：${cleanText(completed.invalidReason, 120) ?? "客户端判定无效"}。`, participant.id, now);
      if (this.state.phase === "race")
        this.updateDriveThroughDeadline(participant, now, false);
      participant.qualifyingFinalLapPending = false;
      participant.practiceFinalLapPending = false;
      this.completeQualifyingIfReady(now);
      this.completePracticeIfReady(now);
      this.touch();
      return accepted();
    }
    const priorFastest = this.fastestLap();
    const bestLapEligible = completed.isBestLapEligible !== false && !participant.lapHasTrackLimitIncident;
    const improvesPersonalBest = bestLapEligible &&
      (participant.bestLapSeconds === null || participant.bestLapSeconds === undefined ||
       completed.lapSeconds < participant.bestLapSeconds - .0005);
    participant.completedLaps++;
    participant.lastLapSeconds = completed.lapSeconds;
    participant.lastLapCompletedAt = now.toISOString();
    if (this.state.phase === "race")
      this.reconcileRaceProgressAtCompletedLap(participant, now);
    if (improvesPersonalBest) {
      participant.bestLapSeconds = completed.lapSeconds;
      participant.bestLapSectorSeconds = this.sanitizeLapSectors(completed.sectorSeconds);
      const sessionNumber = this.state.qualifyingSessionNumber ?? 0;
      if (this.state.phase === "qualifying" && sessionNumber > 0) {
        participant.qualifyingSessionBestLapSeconds ??= [null, null, null];
        participant.qualifyingSessionBestLapSeconds[sessionNumber - 1] = completed.lapSeconds;
      }
      const practiceSessionNumber = this.state.practiceSessionNumber ?? 0;
      if (this.state.phase === "practice" && practiceSessionNumber > 0) {
        participant.practiceSessionBestLapSeconds ??= [null, null, null];
        participant.practiceSessionBestLapSeconds[practiceSessionNumber - 1] = completed.lapSeconds;
      }
    }
    participant.currentLapSeconds = 0;
    participant.currentSector = 0;
    participant.shortcutPenaltyIssued = false;
    participant.progressContinuityReady = false;
    participant.lastSeenAt = now.toISOString();
    participant.disconnectedLapRecoveryUntil = null;
    if (bestLapEligible) this.updateBestSectors(participant, completed.sectorSeconds);
    participant.lapHasTrackLimitIncident = false;
    participant.qualifyingFinalLapPending = false;
    participant.practiceFinalLapPending = false;
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
      if (participant.status === "finished") {
        this.finalizePendingPenaltiesAtFinish(participant, now);
        this.enforceMinimumPitStopsAtFinish(participant, now);
      }
      else this.updateDriveThroughDeadline(participant, now, false);
      this.tryCompleteRaceIfReady(now);
    }
    this.completeQualifyingIfReady(now);
    this.completePracticeIfReady(now);
    this.touch();
    return accepted();
  }

  roomSettings(): RoomSettings {
    return {
      sessionName: this.state.sessionName,
      totalRaceLaps: this.state.totalRaceLaps,
      sectorCount: this.state.sectorCount,
      automaticYellowEnabled: this.state.automaticYellowEnabled,
      automaticCollisionInvestigationsEnabled: this.state.automaticCollisionInvestigationsEnabled,
      disconnectedLapRecoveryEnabled: this.state.disconnectedLapRecoveryEnabled,
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
      ,minimumRequiredPitStops: this.state.minimumRequiredPitStops
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
    this.state.minimumRequiredPitStops = clampInteger(command.minimumRequiredPitStops ?? 1, 0, 20);
    this.state.sectorCount = clampInteger(command.sectorCount, 1, 20);
    this.state.automaticYellowEnabled = Boolean(command.automaticYellowEnabled);
    this.state.automaticCollisionInvestigationsEnabled = command.automaticCollisionInvestigationsEnabled === true;
    this.state.disconnectedLapRecoveryEnabled = command.disconnectedLapRecoveryEnabled === true;
    if (!this.state.disconnectedLapRecoveryEnabled)
      for (const participant of this.state.participants)
        participant.disconnectedLapRecoveryUntil = null;
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
    if (!this.state.automaticCollisionInvestigationsEnabled) {
      for (const participant of this.state.participants) {
        participant.lastProcessedImpactSequence = participant.lastReportedImpactSequence ?? participant.lastProcessedImpactSequence ?? 0;
        participant.lastImpactAt = null;
        participant.lastImpactMagnitudeMps = 0;
        participant.lastImpactSpeedLossMps = 0;
        participant.lastImpactSmashableVelDiff = 0;
        participant.lastImpactSmashableMass = 0;
      }
    }
    this.touch();
    this.recordEvent("roomSettings", `房间设置已更新，赛道边界处理为 ${this.state.trackLimitMode}。`, null, now);
    return accepted();
  }

  setAutomaticCollisionInvestigations(enabled: boolean, now = new Date()): CommandResult {
    this.state.automaticCollisionInvestigationsEnabled = enabled;
    if (!enabled) {
      for (const participant of this.state.participants) {
        participant.lastProcessedImpactSequence = participant.lastReportedImpactSequence ?? participant.lastProcessedImpactSequence ?? 0;
        participant.lastImpactAt = null;
        participant.lastImpactMagnitudeMps = 0;
        participant.lastImpactSpeedLossMps = 0;
        participant.lastImpactSmashableVelDiff = 0;
        participant.lastImpactSmashableMass = 0;
      }
      this.collisionPairCooldowns.clear();
      this.collisionTrajectories.clear();
    }
    this.recordEvent("collisionInvestigationSetting",
      enabled ? "赛事总控已启用疑似碰撞自动调查。" : "赛事总控已关闭疑似碰撞自动调查；已有调查仍会保留。",
      null, now);
    this.touch();
    return accepted();
  }

  applySession(command: SessionCommand, now = new Date()): CommandResult {
    const sessionName = cleanText(command.sessionName, 64);
    if (sessionName) this.state.sessionName = sessionName;
    if (command.totalRaceLaps !== null && command.totalRaceLaps !== undefined)
      this.state.totalRaceLaps = clampInteger(command.totalRaceLaps, 1, 999);

    if (this.state.activeResultStageId &&
        (command.phase !== this.state.phase || ["lobby", "practice", "qualifying", "grid", "race"].includes(command.phase))) {
      this.archiveActiveResult(now, this.currentResultIsComplete());
      this.state.activeResultStageId = null;
    }

    switch (command.phase) {
      case "lobby":
        this.resetCompetitiveState();
        this.state.phase = "lobby";
        this.state.flag = "green";
        this.state.flagMessage = null;
        this.state.activeResultStageId = null;
        break;
      case "practice":
        if (this.tryStartNextPracticeSession(now)) break;
        this.resetCompetitiveState();
        this.configurePractice(command);
        this.state.phase = "practice";
        this.state.activeResultStageId = crypto.randomUUID();
        this.state.flag = "green";
        this.state.practiceSessionNumber = 1;
        this.state.practiceTimeExpired = false;
        this.state.practiceEndsAt = new Date(
          now.getTime() + this.state.practiceSessionMinutes![0] * 60_000).toISOString();
        for (const participant of this.state.participants) {
          participant.status = participant.isConnected ? "onTrack" : "disconnected";
          participant.isReady = false;
          participant.practiceFinalLapPending = false;
          participant.practiceSessionBestLapSeconds = [null, null, null];
        }
        this.state.banner = this.newBanner(
          "information", `${this.practiceSessionLabel()} 开始`,
          `${this.state.practiceSessionMinutes![0]} 分钟`, null, 5_000, now);
        break;
      case "qualifying":
        if (this.tryStartNextQualifyingSession(now)) break;
        this.resetCompetitiveState();
        this.configureQualifying(command);
        this.state.phase = "qualifying";
        this.state.activeResultStageId = crypto.randomUUID();
        this.state.flag = "green";
        this.state.qualifyingSessionNumber = 1;
        this.state.qualifyingTimeExpired = false;
        this.state.qualifyingEndsAt = new Date(
          now.getTime() + this.state.qualifyingSessionMinutes![0] * 60_000).toISOString();
        for (const participant of this.state.participants) {
          participant.status = participant.isConnected ? "onTrack" : "disconnected";
          participant.isReady = false;
          participant.qualifyingEligible = participant.isConnected;
          participant.qualifyingEliminatedInSession = null;
          participant.qualifyingSessionBestLapSeconds = [null, null, null];
        }
        this.state.banner = this.newBanner(
          "information",
          this.state.qualifyingSessionCount === 1 ? "排位赛开始" : "Q1 开始",
          this.state.qualifyingSessionCount === 1
            ? this.state.sessionName
            : `${this.state.qualifyingSessionMinutes![0]} 分钟 · 本节淘汰 ${this.state.qualifyingEliminationCounts![0]} 人`,
          null, 5_000, now);
        break;
      case "grid":
        this.captureCurrentQualifyingResults();
        this.state.phase = "grid";
        this.state.activeResultStageId = null;
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
        this.state.activeResultStageId = crypto.randomUUID();
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
        isPostRaceAdjustment: command.kind === "time" && this.requiresPostRaceAdjustment(participant),
        isAutomatic: false,
        investigationId: null
      };
    this.state.penalties.push(penalty);
    if (command.kind === "disqualification") participant.status = "disqualified";
    this.state.banner = this.newBanner(
      "penalty", `处罚 · ${participant.displayName}`, penaltyDescription(penalty), participant.id, 10_000, now);
    this.recordEvent("manualPenalty", `${participant.displayName}：${penaltyDescription(penalty)}；${reason}。`, participant.id, now);
    this.touch();
    return accepted();
  }

  updatePenalty(command: PenaltyUpdateCommand, now = new Date()): CommandResult {
    const penalty = this.state.penalties.find(item => item.id === command.penaltyId);
    if (!penalty) return rejected("处罚记录不存在。");
    const participant = this.find(penalty.participantId);
    if (!participant) return rejected("车手不存在。");
    if (penalty.kind === "time" && command.valueSeconds !== null && command.valueSeconds !== undefined)
      penalty.valueSeconds = clampInteger(command.valueSeconds, 1, 60);
    const reason = cleanText(command.reason, 240);
    if (reason) penalty.reason = reason;
    if (command.isRevoked) {
      penalty.isRevoked = true;
      if (penalty.kind === "disqualification" && participant.status === "disqualified")
        participant.status = participant.finishedAt ? "finished" : "onTrack";
      if (this.pendingTimePenaltySeconds(participant.id) <= 0 && !this.hasPendingDriveThrough(participant.id))
        this.resetLivePenaltyServiceState(participant);
    }
    this.recordEvent(command.isRevoked ? "penaltyRevoked" : "penaltyUpdated",
      command.isRevoked
        ? `赛事总控取消了 ${participant.displayName} 的处罚：${penaltyDescription(penalty)}。`
        : `赛事总控修改了 ${participant.displayName} 的处罚：${penaltyDescription(penalty)}。`,
      participant.id, now);
    this.touch();
    return accepted();
  }

  resolveInvestigation(command: InvestigationCommand, now = new Date()): CommandResult {
    const investigation = (this.state.investigations ?? []).find(item => item.id === command.investigationId);
    if (!investigation) return rejected("调查记录不存在。");
    if (investigation.status !== "pending") return rejected("该调查已经处理。");
    const participantId = command.participantId ?? investigation.participantId;
    const relatedParticipantIds = investigation.relatedParticipantIds?.length
      ? investigation.relatedParticipantIds : [investigation.participantId];
    if (!relatedParticipantIds.includes(participantId)) return rejected("所选车手不在该调查事件中。");
    const participant = this.find(participantId);
    if (!participant) return rejected("车手不存在。");
    if (!command.applyPenalty) {
      investigation.status = "dismissed";
      investigation.resolvedAt = now.toISOString();
      this.recordEvent("investigationDismissed",
        `赛事总控结束对 ${participant.displayName} 的调查，不予处罚：${investigation.offense}。`,
        participant.id, now);
      this.touch();
      return accepted();
    }

    const kind = command.kind ?? "time";
    if (!allowedPenalties.has(kind) || kind === "gridDrop" || kind === "stopAndGo")
      return rejected("调查处理不支持该处罚类型。");
    const reason = cleanText(command.reason, 240) ?? investigation.offense;
    const penalty: PenaltySnapshot = kind === "driveThrough"
      ? { ...this.createDriveThroughPenalty(participant, reason, now), investigationId: investigation.id }
      : {
        id: crypto.randomUUID(), participantId: participant.id, kind,
        valueSeconds: kind === "time" ? clampInteger(command.valueSeconds ?? 5, 1, 60) : null,
        gridPlaces: null, reason, issuedAt: now.toISOString(), isServed: false, isRevoked: false,
        isPostRaceAdjustment: kind === "time" && this.requiresPostRaceAdjustment(participant),
        isAutomatic: false, investigationId: investigation.id
      };
    this.state.penalties.push(penalty);
    if (kind === "disqualification") participant.status = "disqualified";
    investigation.status = "penalized";
    investigation.penaltyId = penalty.id;
    investigation.resolvedAt = now.toISOString();
    this.state.banner = this.newBanner(
      "penalty", `调查结论 · ${participant.displayName}`, penaltyDescription(penalty), participant.id, 8_000, now);
    this.recordEvent("investigationPenalized",
      `赛事总控确认 ${participant.displayName} 的调查事件并下发：${penaltyDescription(penalty)}。`,
      participant.id, now);
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

  disconnectAndReleaseClient(clientId: string, now = new Date()): CommandResult {
    const participant = this.find(clientId);
    if (participant) {
      if (participant.reservationActive === false) return rejected("该车手已经由总控断开。");
      const releasedName = participant.displayName;
      this.revokeResumeToken(participant.resumeToken);
      participant.reservationActive = false;
      participant.isConnected = false;
      participant.displayName = `${releasedName} · 已断开`;
      if (!terminal(participant.status))
        participant.status = this.state.phase === "race" ? "didNotFinish" : "disconnected";
      if (this.state.phase === "race") participant.finishedAt ??= now.toISOString();
      participant.automaticYellowActive = false;
      participant.hazardCandidateStartedAt = null;
      participant.hazardRecoveryStartedAt = null;
      participant.disconnectedLapRecoveryUntil = null;
      participant.qualifyingFinalLapPending = false;
      participant.practiceFinalLapPending = false;
      this.completeQualifyingIfReady(now);
      this.completePracticeIfReady(now);
      this.refreshYellowFlag(now);
      this.tryCompleteRaceIfReady(now);
      this.recordEvent("participantRemoved", `赛事总控断开了 ${releasedName}，显示名称已释放。`, participant.id, now);
      this.touch();
      return accepted();
    }
    const observerIndex = this.state.observers.findIndex(observer => observer.id === clientId);
    if (observerIndex < 0) return rejected("客户端不存在或已经离开房间。");
    const [observer] = this.state.observers.splice(observerIndex, 1);
    this.revokeResumeToken(observer.resumeToken);
    this.recordEvent("observerRemoved", `赛事总控断开了 OB ${observer.displayName}，显示名称已释放。`, observer.id, now);
    this.touch();
    return accepted();
  }

  tick(now = new Date()): boolean {
    let changed = this.expireDisconnectedLapRecoveries(now);
    if (this.state.phase === "countdown" && this.state.startsAt && this.state.startSequenceAt) {
      if (now.getTime() >= Date.parse(this.state.startsAt)) {
        this.state.phase = "race";
        this.state.activeResultStageId = crypto.randomUUID();
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
      const sessionLabel = this.qualifyingSessionLabel();
      this.state.banner = this.newBanner(
        "chequeredFlag", (this.state.qualifyingSessionCount ?? 1) === 1 ? "排位赛计时结束" : `${sessionLabel} 计时结束`,
        pendingCount > 0 ? `仍有 ${pendingCount} 名车手可完成最后飞驰圈` : "成绩已冻结", null, 8_000, now);
      this.recordEvent("qualifyingExpired", `${sessionLabel} 计时结束，${pendingCount} 名车手仍可完成最后飞驰圈。`, null, now);
      this.completeQualifyingIfReady(now);
      changed = true;
    }
    if (this.state.phase === "practice" && !this.state.practiceTimeExpired && this.state.practiceEndsAt &&
        now.getTime() >= Date.parse(this.state.practiceEndsAt)) {
      this.state.flag = "chequered";
      this.state.practiceTimeExpired = true;
      for (const participant of this.state.participants)
        participant.practiceFinalLapPending = this.eligibleForFinalPracticeLap(participant);
      const pendingCount = this.state.participants.filter(participant => participant.practiceFinalLapPending).length;
      const sessionLabel = this.practiceSessionLabel();
      this.state.banner = this.newBanner(
        "chequeredFlag", `${sessionLabel} 计时结束`,
        pendingCount > 0 ? `仍有 ${pendingCount} 名车手可完成最后一圈` : "本节成绩已冻结",
        null, 8_000, now);
      this.recordEvent("practiceExpired", `${sessionLabel} 计时结束，${pendingCount} 名车手仍可完成最后一圈。`, null, now);
      this.completePracticeIfReady(now);
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
      this.state.phase === "practice" && !this.state.practiceTimeExpired ? this.state.practiceEndsAt : null,
      this.state.banner?.expiresAt
    ]
      .filter((value): value is string => Boolean(value))
      .map(value => Date.parse(value))
      .filter(Number.isFinite);
    values.push(...this.state.participants
      .map(participant => participant.disconnectedLapRecoveryUntil
        ? Date.parse(participant.disconnectedLapRecoveryUntil)
        : Number.NaN)
      .filter(Number.isFinite));
    if (this.state.phase === "countdown" && this.state.startSequenceAt && this.state.illuminatedStartLights < 5) {
      const sequenceAt = Date.parse(this.state.startSequenceAt);
      values.push(sequenceAt + this.state.illuminatedStartLights * 1_000);
    }
    return values.length === 0 ? null : Math.min(...values);
  }

  snapshot(now = new Date()): SessionSnapshot {
    const ordered = this.orderParticipants(now).filter(participant => participant.reservationActive !== false);
    const leader = ordered[0];
    let prior: ParticipantState | undefined;
    const participants: ParticipantSnapshot[] = ordered.map((participant, index) => {
      const displayedBestLap = this.qualifyingDisplayedBestLap(participant);
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
      const qualifying = this.state.phase === "practice" ||
        this.state.phase === "qualifying" || this.state.phase === "grid";
      const leaderBestLap = leader ? this.qualifyingDisplayedBestLap(leader) : null;
      const priorBestLap = prior ? this.qualifyingDisplayedBestLap(prior) : null;
      const gapToLeaderSeconds = qualifying
        ? displayedBestLap !== null && displayedBestLap !== undefined &&
          leaderBestLap !== null && leaderBestLap !== undefined
          ? displayedBestLap - leaderBestLap : null
        : leader ? this.raceDeltaSeconds(leader, participant, now) : null;
      const intervalSeconds = qualifying
        ? displayedBestLap !== null && displayedBestLap !== undefined &&
          priorBestLap !== null && priorBestLap !== undefined
          ? displayedBestLap - priorBestLap : null
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
        bestLapSeconds: displayedBestLap,
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
        isServingDriveThrough: Boolean(participant.driveThroughVisitActive && participant.isInPitLane),
        qualifyingEligible: participant.qualifyingEligible !== false,
        qualifyingEliminatedInSession: participant.qualifyingEliminatedInSession ?? null,
        qualifyingSessionBestLapSeconds: [...(participant.qualifyingSessionBestLapSeconds ?? [null, null, null])],
        practiceFinalLapPending: participant.practiceFinalLapPending ?? false,
        practiceSessionBestLapSeconds: [...(participant.practiceSessionBestLapSeconds ?? [null, null, null])]
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
      observers: [...this.state.observers]
        .sort((left, right) => Date.parse(left.connectedAt) - Date.parse(right.connectedAt))
        .map(observer => ({
          id: observer.id,
          displayName: observer.displayName,
          connectedAt: observer.connectedAt
        })),
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
      chequeredImminent: this.state.chequeredImminent,
      penalties: this.state.penalties.map(item => ({ ...item })),
      investigations: (this.state.investigations ?? []).map(item => ({ ...item })),
      qualifyingSessionNumber: this.state.qualifyingSessionNumber ?? 0,
      qualifyingSessionCount: this.state.qualifyingSessionCount ?? 1,
      qualifyingSessionMinutes: [...(this.state.qualifyingSessionMinutes ?? [10])],
      qualifyingEliminationCounts: [...(this.state.qualifyingEliminationCounts ?? [])],
      practiceEndsAt: this.state.practiceEndsAt ?? null,
      practiceTimeExpired: this.state.practiceTimeExpired ?? false,
      practiceSessionNumber: this.state.practiceSessionNumber ?? 0,
      practiceSessionCount: this.state.practiceSessionCount ?? 1,
      practiceSessionMinutes: [...(this.state.practiceSessionMinutes ?? [60])]
      ,minimumRequiredPitStops: this.state.minimumRequiredPitStops
      ,disconnectedLapRecoveryEnabled: this.state.disconnectedLapRecoveryEnabled
    };
  }

  private normalizeStored(stored: StoredRaceState): StoredRaceState {
    return {
      ...stored,
      revision: clampInteger(stored.revision, 1, Number.MAX_SAFE_INTEGER),
      sessionName: cleanText(stored.sessionName, 64) ?? "地产赛事",
      phaseBeforeSuspension: stored.phaseBeforeSuspension ?? "race",
      totalRaceLaps: clampInteger(stored.totalRaceLaps, 1, 999),
      minimumRequiredPitStops: clampInteger(stored.minimumRequiredPitStops ?? 1, 0, 20),
      startsAt: stored.startsAt ?? null,
      startSequenceAt: stored.startSequenceAt ?? null,
      raceSuspendedAt: stored.raceSuspendedAt ?? null,
      raceSuspendedMilliseconds: clamp(stored.raceSuspendedMilliseconds ?? 0, 0, Number.MAX_SAFE_INTEGER),
      raceEndedAt: stored.raceEndedAt ?? null,
      illuminatedStartLights: clampInteger(stored.illuminatedStartLights ?? 0, 0, 5),
      startLightsOut: stored.startLightsOut ?? false,
      qualifyingEndsAt: stored.qualifyingEndsAt ?? null,
      qualifyingTimeExpired: stored.qualifyingTimeExpired ?? false,
      qualifyingSessionNumber: clampInteger(stored.qualifyingSessionNumber ?? 0, 0, 3),
      qualifyingSessionCount: clampInteger(stored.qualifyingSessionCount ?? 1, 1, 3),
      qualifyingSessionMinutes: Array.isArray(stored.qualifyingSessionMinutes)
        ? stored.qualifyingSessionMinutes.slice(0, 3).map(value => clampInteger(value, 1, 180))
        : [10],
      qualifyingEliminationCounts: Array.isArray(stored.qualifyingEliminationCounts)
        ? stored.qualifyingEliminationCounts.slice(0, 2).map(value => clampInteger(value, 0, 11))
        : [],
      practiceEndsAt: stored.practiceEndsAt ?? null,
      practiceTimeExpired: stored.practiceTimeExpired ?? false,
      practiceSessionNumber: clampInteger(stored.practiceSessionNumber ?? 0, 0, 3),
      practiceSessionCount: clampInteger(stored.practiceSessionCount ?? 1, 1, 3),
      practiceSessionMinutes: Array.isArray(stored.practiceSessionMinutes)
        ? stored.practiceSessionMinutes.slice(0, 3).map(value => clampInteger(value, 1, 180))
        : [60],
      banner: stored.banner ?? null,
      participants: Array.isArray(stored.participants) ? stored.participants.slice(0, 12).map(participant => ({
        ...participant,
        reservationActive: participant.reservationActive ?? true,
        qualifyingFinalLapPending: participant.qualifyingFinalLapPending ?? false,
        qualifyingEligible: participant.qualifyingEligible ?? true,
        qualifyingEliminatedInSession: participant.qualifyingEliminatedInSession ?? null,
        qualifyingSessionBestLapSeconds: Array.isArray(participant.qualifyingSessionBestLapSeconds)
          ? [...participant.qualifyingSessionBestLapSeconds.slice(0, 3), null, null, null].slice(0, 3)
          : [null, null, null],
        practiceFinalLapPending: participant.practiceFinalLapPending ?? false,
        practiceSessionBestLapSeconds: Array.isArray(participant.practiceSessionBestLapSeconds)
          ? [...participant.practiceSessionBestLapSeconds.slice(0, 3), null, null, null].slice(0, 3)
          : [null, null, null],
        falseStartArmedAt: participant.falseStartArmedAt ?? null,
        falseStartReferenceProgress: participant.falseStartReferenceProgress ?? null,
        falseStartMovementStartedAt: participant.falseStartMovementStartedAt ?? null,
        falseStartPenalized: participant.falseStartPenalized ?? false,
        lastLapCompletedAt: participant.lastLapCompletedAt ?? null,
        disconnectedLapRecoveryUntil: participant.disconnectedLapRecoveryUntil ?? null,
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
        lastShortcutEvidenceId: participant.lastShortcutEvidenceId ?? null,
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
        telemetryValid: participant.telemetryValid ?? false,
        hasWorldPosition: participant.hasWorldPosition ?? false,
        lastTelemetryReceivedAt: participant.lastTelemetryReceivedAt ?? null,
        isApproachingPit: participant.isApproachingPit ?? false,
        isOnPitRoute: participant.isOnPitRoute ?? false,
        lastReportedImpactSequence: clampInteger(participant.lastReportedImpactSequence ?? 0, 0, Number.MAX_SAFE_INTEGER),
        lastProcessedImpactSequence: clampInteger(participant.lastProcessedImpactSequence ?? 0, 0, Number.MAX_SAFE_INTEGER),
        lastImpactAt: participant.lastImpactAt ?? null,
        lastImpactMagnitudeMps: clamp(participant.lastImpactMagnitudeMps ?? 0, 0, 200),
        lastImpactSpeedLossMps: clamp(participant.lastImpactSpeedLossMps ?? 0, 0, 200),
        lastImpactSmashableVelDiff: clamp(participant.lastImpactSmashableVelDiff ?? 0, 0, 200),
        lastImpactSmashableMass: clamp(participant.lastImpactSmashableMass ?? 0, 0, 100_000),
        teamId: cleanText(participant.teamId, 40),
        teamColor: isThemeColor(participant.teamColor) ? participant.teamColor.toUpperCase() : null
      })) : [],
      observers: Array.isArray(stored.observers)
        ? stored.observers.slice(0, maximumObservers).map(observer => ({
          id: cleanText(observer.id, 80) ?? crypto.randomUUID(),
          resumeToken: cleanText(observer.resumeToken, 256) ?? createResumeToken(),
          displayName: cleanText(observer.displayName, 20) ?? "OB",
          connectedAt: Number.isFinite(Date.parse(observer.connectedAt))
            ? observer.connectedAt
            : new Date().toISOString()
        }))
        : [],
      penalties: Array.isArray(stored.penalties)
        ? stored.penalties.map(penalty => ({
          ...penalty,
          isPostRaceAdjustment: penalty.isPostRaceAdjustment ?? false,
          isAutomatic: penalty.isAutomatic ?? false,
          investigationId: penalty.investigationId ?? null
        }))
        : [],
      investigations: Array.isArray(stored.investigations)
        ? stored.investigations.slice(-500).map(item => ({
          ...item,
          offense: cleanText(item.offense, 240) ?? "待总控核查的赛道事件",
          lapNumber: clampInteger(item.lapNumber, 1, 9999),
          status: item.status === "penalized" || item.status === "dismissed" ? item.status : "pending",
          penaltyId: cleanText(item.penaltyId, 80),
          resolvedAt: item.resolvedAt ?? null,
          relatedParticipantIds: Array.isArray(item.relatedParticipantIds)
            ? item.relatedParticipantIds.map(id => cleanText(id, 80)).filter((id): id is string => Boolean(id)).slice(0, 2)
            : null,
          collisionEvidence: item.collisionEvidence ?? null
        }))
        : [],
      receivedLapEvents: Array.isArray(stored.receivedLapEvents) ? stored.receivedLapEvents.slice(-10_000) : []
      ,sectorCount: clampInteger(stored.sectorCount ?? 3, 1, 20)
      ,automaticYellowEnabled: stored.automaticYellowEnabled ?? true
      ,automaticCollisionInvestigationsEnabled: stored.automaticCollisionInvestigationsEnabled ?? false
      ,disconnectedLapRecoveryEnabled: stored.disconnectedLapRecoveryEnabled ?? false
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
      ,revokedResumeTokens: Array.isArray(stored.revokedResumeTokens)
        ? stored.revokedResumeTokens.map(token => cleanText(token, 256)).filter((token): token is string => Boolean(token)).slice(-100)
        : []
      ,activeResultStageId: cleanText(stored.activeResultStageId, 80) ??
        (["practice", "qualifying", "race", "finished", "suspended"].includes(stored.phase)
          ? crypto.randomUUID() : null)
      ,resultHistory: Array.isArray(stored.resultHistory)
        ? stored.resultHistory.slice(-24).map(result => ({
          ...result,
          participants: Array.isArray(result.participants)
            ? result.participants.slice(0, 12).map(participant => ({
              ...participant,
              penalties: Array.isArray(participant.penalties)
                ? participant.penalties.map(penalty => ({ ...penalty })) : []
            })) : []
        }))
        : []
    };
  }

  private revokeResumeToken(token: string): void {
    this.state.revokedResumeTokens ??= [];
    if (!this.state.revokedResumeTokens.includes(token)) this.state.revokedResumeTokens.push(token);
    if (this.state.revokedResumeTokens.length > 100)
      this.state.revokedResumeTokens.splice(0, this.state.revokedResumeTokens.length - 100);
  }

  private trackMatches(request: LoginRequest): boolean {
    return (!this.state.trackId || equalsIgnoreCase(this.state.trackId, request.trackId)) &&
      (!this.state.trackRevision || this.state.trackRevision === request.trackRevision) &&
      (!this.state.trackPackageHash || equalsIgnoreCase(this.state.trackPackageHash, request.trackPackageHash));
  }

  private hasDuplicateName(displayName: string, exceptId?: string): boolean {
    return this.state.participants.some(participant =>
      participant.reservationActive !== false && participant.id !== exceptId &&
      participant.displayName.localeCompare(displayName, undefined, { sensitivity: "accent" }) === 0) ||
      this.state.observers.some(observer =>
        observer.id !== exceptId && observer.displayName.localeCompare(displayName, undefined, { sensitivity: "accent" }) === 0);
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
      participant.reservationActive !== false && participant.id !== exceptId &&
      equalsIgnoreCase(teamId, participant.teamId)).length < this.state.driversPerTeam;
  }

  private selectLegacyTeam(exceptId?: string): TeamDefinition | null {
    const candidates = this.state.teams
      .map((team, index) => ({
        team,
        index,
        members: this.state.participants.filter(participant =>
          participant.reservationActive !== false && participant.id !== exceptId &&
          equalsIgnoreCase(team.id, participant.teamId)).length
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

  private evaluateAutomaticYellow(participant: ParticipantState, now: Date, isOnPitRoute = false): void {
    if (!this.state.automaticYellowEnabled ||
        (this.state.phase !== "race" && this.state.phase !== "practice" && this.state.phase !== "qualifying") ||
        (this.state.phase === "qualifying" && participant.qualifyingEligible === false) || participant.isInPitLane ||
        participant.isInServiceZone || isOnPitRoute || terminal(participant.status) || participant.status === "disconnected") {
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

  private evaluateCollisionInvestigation(participant: ParticipantState, update: TelemetryUpdate, now: Date): void {
    const impactSequence = clampInteger(update.impactSequence ?? 0, 0, Number.MAX_SAFE_INTEGER);
    const incomingEvidenceIsNew = impactSequence > (participant.lastReportedImpactSequence ?? 0);
    if (incomingEvidenceIsNew) {
      participant.lastReportedImpactSequence = impactSequence;
      participant.lastImpactAt = new Date(now.getTime() - clampInteger(update.impactAgeMilliseconds ?? 0, 0, 2_000)).toISOString();
      participant.lastImpactWorldX = clamp(update.impactWorldX, -10_000_000, 10_000_000);
      participant.lastImpactWorldY = clamp(update.impactWorldY, -10_000_000, 10_000_000);
      participant.lastImpactWorldZ = clamp(update.impactWorldZ, -10_000_000, 10_000_000);
      participant.lastImpactMagnitudeMps = clamp(update.impactMagnitudeMps ?? 0, 0, 200);
      participant.lastImpactSpeedLossMps = clamp(update.impactSpeedLossMps ?? 0, 0, 200);
      participant.lastImpactSmashableVelDiff = clamp(update.impactSmashableVelDiff ?? 0, 0, 200);
      participant.lastImpactSmashableMass = clamp(update.impactSmashableMass ?? 0, 0, 100_000);
    }
    if (!this.state.automaticCollisionInvestigationsEnabled) {
      participant.lastProcessedImpactSequence = participant.lastReportedImpactSequence;
      return;
    }
    if (!incomingEvidenceIsNew || impactSequence <= (participant.lastProcessedImpactSequence ?? 0)) return;
    participant.lastProcessedImpactSequence = impactSequence;
    const impactAge = clampInteger(update.impactAgeMilliseconds ?? 0, 0, 2_000);
    const impactMagnitude = clamp(update.impactMagnitudeMps ?? 0, 0, 200);
    const impactSpeedLoss = clamp(update.impactSpeedLossMps ?? 0, 0, 200);
    const collisionSessionActive = this.state.phase === "practice" ||
      this.state.phase === "qualifying" || this.state.phase === "race";
    if (!collisionSessionActive ||
        this.state.flag === "chequered" ||
        update.hasWorldPosition !== true || impactAge > 1_000 ||
        impactMagnitude < RaceCore.minimumCollisionImpactMagnitudeMps ||
        clamp(update.impactSmashableVelDiff ?? 0, 0, 200) >= .2 ||
        clamp(update.impactSmashableMass ?? 0, 0, 100_000) >= .5 ||
        participant.isInPitLane || participant.isInServiceZone ||
        participant.isApproachingPit || participant.isOnPitRoute || terminal(participant.status)) return;

    const incidentAt = new Date(now.getTime() - impactAge);
    let nearest: ParticipantState | null = null;
    let nearestDistance = Number.POSITIVE_INFINITY;
    let nearestVerticalDistance = 0;
    let nearestRelativeSpeed = 0;
    let nearestIncidentX = 0, nearestIncidentY = 0, nearestIncidentZ = 0;
    let nearestApproachDistanceReduction = 0;
    let nearestBothReportedImpact = false;
    let nearestWorldVelocityX = 0, nearestWorldVelocityZ = 0;
    const impactX = clamp(update.impactWorldX, -10_000_000, 10_000_000);
    const impactY = clamp(update.impactWorldY, -10_000_000, 10_000_000);
    const impactZ = clamp(update.impactWorldZ, -10_000_000, 10_000_000);
    for (const candidate of this.state.participants) {
      if (candidate.id === participant.id || candidate.reservationActive === false || !candidate.isConnected ||
          candidate.telemetryValid !== true || candidate.hasWorldPosition !== true || candidate.isInPitLane ||
          candidate.isInServiceZone || candidate.isApproachingPit || candidate.isOnPitRoute || terminal(candidate.status) ||
          !candidate.lastTelemetryReceivedAt || now.getTime() - Date.parse(candidate.lastTelemetryReceivedAt) > 750) continue;
      const candidateImpactAt = candidate.lastImpactAt ? Date.parse(candidate.lastImpactAt) : Number.NaN;
      const pairedImpactCandidate = Number.isFinite(candidateImpactAt) &&
        Math.abs(candidateImpactAt - incidentAt.getTime()) <= 1_000 &&
        (candidate.lastImpactMagnitudeMps ?? 0) >= RaceCore.minimumCollisionImpactMagnitudeMps &&
        (candidate.lastImpactSmashableVelDiff ?? 0) < .2 &&
        (candidate.lastImpactSmashableMass ?? 0) < .5 &&
        Number.isFinite(candidate.lastImpactWorldX) &&
        Number.isFinite(candidate.lastImpactWorldY) &&
        Number.isFinite(candidate.lastImpactWorldZ);
      let candidateSample: CollisionPositionSample | null;
      let candidateX: number, candidateY: number, candidateZ: number;
      if (pairedImpactCandidate) {
        candidateSample = this.closestCollisionPositionSample(candidate.id, candidateImpactAt);
        if (!candidateSample) continue;
        candidateX = candidate.lastImpactWorldX!;
        candidateY = candidate.lastImpactWorldY!;
        candidateZ = candidate.lastImpactWorldZ!;
      } else {
        candidateSample = this.closestCollisionPositionSample(candidate.id, incidentAt.getTime());
        if (!candidateSample) continue;
        candidateX = candidateSample.worldX;
        candidateY = candidateSample.worldY;
        candidateZ = candidateSample.worldZ;
      }
      const horizontalDistance = Math.hypot(impactX - candidateX, impactZ - candidateZ);
      const verticalDistance = Math.abs(impactY - candidateY);
      const maximumHorizontalDistance = pairedImpactCandidate
        ? RaceCore.maximumPairedImpactDistanceMeters
        : RaceCore.maximumCollisionHorizontalDistanceMeters;
      if (horizontalDistance > maximumHorizontalDistance || verticalDistance > 2.5) continue;
      const relativeSpeed = update.hasWorldVelocity === true && candidateSample.hasWorldVelocity
        ? Math.hypot(
          clamp(update.impactWorldVelocityX, -500, 500) - candidateSample.worldVelocityX,
          clamp(update.impactWorldVelocityZ, -500, 500) - candidateSample.worldVelocityZ)
        : 0;
      const approachDistanceReduction = pairedImpactCandidate
        ? 0
        : this.collisionApproachDistanceReduction(
          participant.id, candidate.id, incidentAt.getTime(), horizontalDistance);
      const strongReporterEvidence = impactMagnitude >= RaceCore.strongCollisionImpactMagnitudeMps ||
        impactSpeedLoss >= RaceCore.minimumCollisionSpeedLossMps;
      const pairedImpactConfirmed = pairedImpactCandidate &&
        relativeSpeed >= RaceCore.minimumCollisionRelativeSpeedMps;
      const singleReporterTrajectoryConfirmed = !pairedImpactCandidate &&
        approachDistanceReduction >= RaceCore.minimumCollisionApproachMeters &&
        relativeSpeed >= RaceCore.minimumCollisionRelativeSpeedMps &&
        strongReporterEvidence;
      if (!pairedImpactConfirmed && !singleReporterTrajectoryConfirmed ||
          horizontalDistance >= nearestDistance) continue;
      nearest = candidate;
      nearestDistance = horizontalDistance;
      nearestVerticalDistance = verticalDistance;
      nearestRelativeSpeed = relativeSpeed;
      nearestIncidentX = candidateX;
      nearestIncidentY = candidateY;
      nearestIncidentZ = candidateZ;
      nearestApproachDistanceReduction = approachDistanceReduction;
      nearestBothReportedImpact = pairedImpactConfirmed;
      nearestWorldVelocityX = candidateSample.worldVelocityX;
      nearestWorldVelocityZ = candidateSample.worldVelocityZ;
    }
    if (!nearest) return;
    const pairKey = [participant.id, nearest.id].sort().join(":");
    const lapNumber = Math.max(1, Math.max(participant.completedLaps, nearest.completedLaps) + 1);
    const currentEvidence: CollisionEvidenceSnapshot = {
      incidentAt: incidentAt.toISOString(), reporterParticipantId: participant.id, otherParticipantId: nearest.id,
      reporterName: participant.displayName, otherName: nearest.displayName,
      reporterThemeColor: participant.themeColor, otherThemeColor: nearest.themeColor,
      reporterWorldX: impactX, reporterWorldY: impactY, reporterWorldZ: impactZ,
      otherWorldX: nearestIncidentX, otherWorldY: nearestIncidentY, otherWorldZ: nearestIncidentZ,
      reporterVelocityX: clamp(update.impactWorldVelocityX, -500, 500),
      reporterVelocityZ: clamp(update.impactWorldVelocityZ, -500, 500),
      otherVelocityX: nearestWorldVelocityX, otherVelocityZ: nearestWorldVelocityZ,
      horizontalDistanceMeters: nearestDistance, verticalDistanceMeters: nearestVerticalDistance,
      relativeSpeedKph: nearestRelativeSpeed * 3.6, impactMagnitudeMps: impactMagnitude,
      impactSpeedLossMps: impactSpeedLoss,
      approachDistanceReductionMeters: Math.max(0, nearestApproachDistanceReduction),
      bothDriversReportedImpact: nearestBothReportedImpact,
      contactCount: 1,
      lastIncidentAt: incidentAt.toISOString()
    };
    if (this.tryMergeCollisionInvestigation(pairKey, currentEvidence, lapNumber)) {
      this.collisionPairCooldowns.set(pairKey, now.getTime() + RaceCore.collisionPairCooldownMilliseconds);
      return;
    }
    if ((this.state.investigations ?? []).filter(item => item.collisionEvidence).length >= 24 ||
        (this.collisionPairCooldowns.get(pairKey) ?? 0) > now.getTime()) return;
    this.collisionPairCooldowns.set(pairKey, now.getTime() + RaceCore.collisionPairCooldownMilliseconds);
    const investigation: InvestigationSnapshot = {
      id: crypto.randomUUID(), participantId: participant.id, offense: this.collisionOffense(currentEvidence),
      detectedAt: now.toISOString(), lapNumber, status: "pending",
      relatedParticipantIds: [participant.id, nearest.id],
      collisionEvidence: currentEvidence
    };
    (this.state.investigations ??= []).push(investigation);
    this.state.banner = this.newBanner("information", "正在调查 · 疑似碰撞",
      `${participant.displayName} ↔ ${nearest.displayName} · 第 ${lapNumber} 圈`, null, 8_000, now);
    this.state.banner.isInvestigation = true;
    this.recordEvent("collisionInvestigationOpened",
      `${participant.displayName} 与 ${nearest.displayName} 发生疑似车辆接触，已交由总控调查（第 ${lapNumber} 圈）。`,
      participant.id, now);
  }

  private tryMergeCollisionInvestigation(
    pairKey: string,
    current: CollisionEvidenceSnapshot,
    lapNumber: number): boolean {
    const investigations = this.state.investigations ?? [];
    for (let index = investigations.length - 1; index >= 0; index--) {
      const existing = investigations[index], previous = existing.collisionEvidence;
      if (existing.status !== "pending" || !previous ||
          [previous.reporterParticipantId, previous.otherParticipantId].sort().join(":") !== pairKey) continue;
      const previousLastAt = Date.parse(previous.lastIncidentAt ?? previous.incidentAt);
      const currentAt = Date.parse(current.incidentAt);
      if (!Number.isFinite(previousLastAt) || !Number.isFinite(currentAt) ||
          Math.abs(currentAt - previousLastAt) > RaceCore.collisionPairCooldownMilliseconds) continue;

      const useCurrentGeometry = current.impactMagnitudeMps > previous.impactMagnitudeMps ||
        current.horizontalDistanceMeters < previous.horizontalDistanceMeters;
      const geometry = useCurrentGeometry ? current : previous;
      const firstAt = Math.min(Date.parse(previous.incidentAt), currentAt);
      const lastAt = Math.max(previousLastAt, currentAt);
      const merged: CollisionEvidenceSnapshot = {
        ...geometry,
        incidentAt: new Date(firstAt).toISOString(),
        lastIncidentAt: new Date(lastAt).toISOString(),
        contactCount: clampInteger((previous.contactCount ?? 1) + 1, 1, 99),
        horizontalDistanceMeters: Math.min(previous.horizontalDistanceMeters, current.horizontalDistanceMeters),
        verticalDistanceMeters: Math.min(previous.verticalDistanceMeters, current.verticalDistanceMeters),
        relativeSpeedKph: Math.max(previous.relativeSpeedKph, current.relativeSpeedKph),
        impactMagnitudeMps: Math.max(previous.impactMagnitudeMps, current.impactMagnitudeMps),
        impactSpeedLossMps: Math.max(previous.impactSpeedLossMps, current.impactSpeedLossMps),
        approachDistanceReductionMeters: Math.max(
          previous.approachDistanceReductionMeters ?? 0,
          current.approachDistanceReductionMeters ?? 0),
        bothDriversReportedImpact:
          previous.bothDriversReportedImpact === true || current.bothDriversReportedImpact === true
      };
      investigations[index] = {
        ...existing,
        offense: this.collisionOffense(merged),
        lapNumber: Math.min(existing.lapNumber, lapNumber),
        collisionEvidence: merged
      };
      return true;
    }
    return false;
  }

  private collisionOffense(evidence: CollisionEvidenceSnapshot): string {
    const count = Math.max(1, evidence.contactCount ?? 1);
    const durationSeconds = Math.max(0,
      (Date.parse(evidence.lastIncidentAt ?? evidence.incidentAt) - Date.parse(evidence.incidentAt)) / 1_000);
    const prefix = count > 1
      ? `连续疑似车辆接触（${count} 次，${durationSeconds.toFixed(1)} 秒内）`
      : "疑似车辆接触";
    return `${prefix}：${evidence.reporterName} 与 ${evidence.otherName}；最近距离 ${evidence.horizontalDistanceMeters.toFixed(1)} m，运动突变 ${evidence.impactMagnitudeMps.toFixed(1)} m/s，相对速度 ${evidence.relativeSpeedKph.toFixed(0)} km/h，接触前距离收窄 ${Math.max(0, evidence.approachDistanceReductionMeters ?? 0).toFixed(1)} m。仅供总控结合画面核查，不代表责任判定`;
  }

  private recordCollisionPositionSample(participant: ParticipantState, update: TelemetryUpdate, now: Date): void {
    if (update.hasWorldPosition !== true) return;
    const samples = this.collisionTrajectories.get(participant.id) ?? [];
    samples.push({
      at: now.getTime(),
      worldX: clamp(update.worldX, -10_000_000, 10_000_000),
      worldY: clamp(update.worldY, -10_000_000, 10_000_000),
      worldZ: clamp(update.worldZ, -10_000_000, 10_000_000),
      hasWorldVelocity: update.hasWorldVelocity === true,
      worldVelocityX: clamp(update.worldVelocityX, -500, 500),
      worldVelocityY: clamp(update.worldVelocityY, -500, 500),
      worldVelocityZ: clamp(update.worldVelocityZ, -500, 500)
    });
    const minimumAt = now.getTime() - RaceCore.collisionTrajectoryLifetimeMilliseconds;
    while (samples.length > 0 && samples[0].at < minimumAt) samples.shift();
    if (samples.length > 32) samples.splice(0, samples.length - 32);
    this.collisionTrajectories.set(participant.id, samples);
  }

  private closestCollisionPositionSample(participantId: string, target: number): CollisionPositionSample | null {
    const samples = this.collisionTrajectories.get(participantId) ?? [];
    let nearest: CollisionPositionSample | null = null;
    let difference = Number.POSITIVE_INFINITY;
    for (const sample of samples) {
      const candidateDifference = Math.abs(sample.at - target);
      if (candidateDifference >= difference) continue;
      nearest = sample;
      difference = candidateDifference;
    }
    return difference <= RaceCore.collisionTrajectoryMatchToleranceMilliseconds ? nearest : null;
  }

  private collisionApproachDistanceReduction(
    reporterId: string,
    otherId: string,
    incidentAt: number,
    incidentDistance: number): number {
    const lookbackAt = incidentAt - RaceCore.collisionApproachLookbackMilliseconds;
    const reporter = this.closestCollisionPositionSample(reporterId, lookbackAt);
    const other = this.closestCollisionPositionSample(otherId, lookbackAt);
    if (!reporter || !other) return 0;
    return Math.hypot(reporter.worldX - other.worldX, reporter.worldZ - other.worldZ) - incidentDistance;
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
      reason: "抢跑：五盏红灯熄灭前车辆已经移动", issuedAt: now.toISOString(), isServed: false, isRevoked: false,
      isAutomatic: true
    };
    this.state.penalties.push(penalty);
    this.state.banner = this.newBanner("penalty", `抢跑 · ${participant.displayName}`, "自动加罚 5 秒", participant.id, 8_000, now);
    this.recordEvent("falseStart", `${participant.displayName} 抢跑，记录 5 秒待执行罚时。`, participant.id, now);
    this.touch();
  }

  private updatePitServiceState(participant: ParticipantState, update: TelemetryUpdate): void {
    participant.isInPitLane = Boolean(update.isInPitLane);
    participant.isInServiceZone = participant.isInPitLane && Boolean(update.isInServiceZone);
    const serviceBlocked = this.canExecutePenalties(participant) &&
      (this.pendingTimePenaltySeconds(participant.id) > 0 || Boolean(participant.penaltyServiceActive));
    participant.pitServiceElapsedSeconds = participant.isInServiceZone && !serviceBlocked
      ? clamp(update.pitServiceElapsedSeconds, 0, 86_400)
      : 0;
    participant.pitLaneElapsedSeconds = clamp(update.pitLaneElapsedSeconds ?? 0, 0, 86_400);
    participant.pitServiceRequirementMet = participant.isInServiceZone && !serviceBlocked &&
      Boolean(update.pitServiceRequirementMet);
    const reportedServices = clampInteger(update.completedPitServices, 0, 999);
    if (participant.pitServiceRequirementMet && reportedServices === participant.completedPitServices + 1)
      participant.completedPitServices++;
  }

  private canExecutePenalties(participant: ParticipantState): boolean {
    return this.state.phase === "race" && this.state.flag !== "chequered" &&
      !terminal(participant.status) && participant.status !== "disconnected";
  }

  private requiresPostRaceAdjustment(participant: ParticipantState): boolean {
    return this.state.phase === "finished" ||
      this.state.phase === "race" && this.state.flag === "chequered" ||
      participant.status === "finished" || participant.status === "didNotFinish" ||
      participant.status === "disqualified";
  }

  private resetLivePenaltyServiceState(participant: ParticipantState): void {
    participant.penaltyServiceActive = false;
    participant.penaltyServiceAttempted = false;
    participant.penaltyServiceElapsedSeconds = 0;
    participant.penaltyServiceRequiredSeconds = 0;
    participant.penaltyServiceLastUpdatedAt = null;
    participant.driveThroughVisitActive = false;
    participant.driveThroughStopCandidateStartedAt = null;
    participant.pitVisitHadServiceStop = false;
    participant.pitVisitPaused = false;
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
    const clientEvidenceHandled = this.evaluateClientShortcutEvidence(participant, update, now);
    const monotonic = Number.isFinite(update.clientMonotonicMilliseconds)
      ? update.clientMonotonicMilliseconds : 0;
    const trackLength = update.trackLengthMeters && update.trackLengthMeters > 0
      ? clamp(update.trackLengthMeters, 50, 100_000) : 0;
    if (!clientEvidenceHandled && participant.progressContinuityReady && trackLength >= 50 &&
        monotonic > (participant.lastTelemetryMonotonicMilliseconds ?? 0)) {
      const elapsedSeconds = (monotonic - (participant.lastTelemetryMonotonicMilliseconds ?? 0)) / 1_000;
      let progressDelta = clamp(update.trackProgress, 0, 1) - (participant.lastContinuityProgress ?? 0);
      if (progressDelta < -.75) progressDelta += 1;
      const routeDistance = progressDelta * trackLength;
      const reportedSpeed = Math.max(participant.speedKph, clamp(update.speedKph, 0, 800)) / 3.6;
      const plausibleDistance = Math.max(60, reportedSpeed * elapsedSeconds * 3 + 30);
      if ((this.state.phase === "race" || this.state.phase === "practice" || this.state.phase === "qualifying") &&
          (this.state.phase !== "qualifying" || participant.qualifyingEligible !== false) &&
          !participant.isInPitLane && !participant.isInServiceZone && !update.isApproachingPit && !update.isOnPitRoute &&
          !terminal(participant.status) && participant.status !== "disconnected" &&
          elapsedSeconds > 0 && elapsedSeconds <= 2 && progressDelta > 0 && progressDelta < .75 &&
          routeDistance > plausibleDistance && !participant.shortcutPenaltyIssued) {
        participant.shortcutPenaltyIssued = true;
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

  private evaluateClientShortcutEvidence(
    participant: ParticipantState,
    update: TelemetryUpdate,
    now: Date): boolean {
    const evidence = update.shortcutEvidence;
    if (!evidence || typeof evidence.id !== "string" || evidence.id.length < 32 ||
        evidence.id === participant.lastShortcutEvidenceId)
      return false;
    participant.lastShortcutEvidenceId = evidence.id.slice(0, 64);

    const eligible = (this.state.phase === "race" || this.state.phase === "practice" ||
        this.state.phase === "qualifying") &&
      (this.state.phase !== "qualifying" || participant.qualifyingEligible !== false) &&
      !participant.isInPitLane && !participant.isInServiceZone &&
      update.isApproachingPit !== true && update.isOnPitRoute !== true &&
      !terminal(participant.status) && participant.status !== "disconnected";
    const detectedAt = clamp(evidence.detectedAtMonotonicMilliseconds, 0, Number.MAX_SAFE_INTEGER);
    const ageMilliseconds = clamp(update.clientMonotonicMilliseconds, 0, Number.MAX_SAFE_INTEGER) - detectedAt;
    const routeDistance = clamp(evidence.routeDistanceMeters, 0, 1_000);
    const worldDistance = clamp(evidence.worldDistanceMeters, 0, 1_000);
    const reportedGain = clamp(evidence.gainMeters, 0, 1_000);
    const calculatedGain = routeDistance - worldDistance;
    const confidence = clamp(evidence.confidence, 0, 1);
    const protectedRoute = clamp(evidence.protectedRouteMeters, 0, 1_000);
    const missedGates = clampInteger(evidence.missedCriticalGates, 0, 32);
    const flags = clampInteger(evidence.flags, 0, 255);
    const trackTolerance = update.trackToleranceMeters && update.trackToleranceMeters > 0
      ? clamp(update.trackToleranceMeters, 4, 50) : 18;
    const minimumGain = Math.max(5, trackTolerance * .3);
    const gainTolerance = Math.max(3, reportedGain * .25);
    const hasDistanceGain = (flags & 1) !== 0;
    const hasRouteSupport = (flags & (2 | 4)) !== 0;
    if (!eligible || ageMilliseconds < 0 || ageMilliseconds > 10_000 || confidence < .72 ||
        !hasDistanceGain || !hasRouteSupport || routeDistance < 8 || calculatedGain < minimumGain ||
        reportedGain < minimumGain || Math.abs(calculatedGain - reportedGain) > gainTolerance ||
        protectedRoute > routeDistance + 2)
      return false;

    const missedGate = (flags & 4) !== 0 && missedGates > 0;
    const ambiguous = (flags & 8) !== 0;
    const severe = (!ambiguous && confidence >= .85 && missedGate &&
        reportedGain >= Math.max(10, trackTolerance * .45)) ||
      (confidence >= .8 && reportedGain >= 25);
    if (participant.trackLimitExcursionStartedAt)
      participant.trackLimitSeverePenaltyIssued = true;
    this.registerTrackLimitIncident(
      participant,
      severe,
      `绕过约 ${protectedRoute.toFixed(0)} 米弯道路程，实走 ${worldDistance.toFixed(1)} 米，` +
      `获得约 ${reportedGain.toFixed(1)} 米距离优势` +
      (missedGate ? `，未通过 ${missedGates} 个关键门` : ""),
      now);
    return true;
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
    if ((this.state.phase !== "race" && this.state.phase !== "practice" && this.state.phase !== "qualifying") ||
        (this.state.phase === "qualifying" && participant.qualifyingEligible === false) ||
        participant.isInPitLane || participant.isInServiceZone || update.isApproachingPit || update.isOnPitRoute ||
        terminal(participant.status) || participant.status === "disconnected") {
      this.resetTrackLimitExcursion(participant);
      return;
    }
    const minorOffsetMeters = clamp(participant.trackToleranceMeters ?? 18, 6, 30);
    const severeOffsetMeters = Math.max(minorOffsetMeters + 6, this.state.severeLateralOffsetMeters);
    const absoluteOffset = Math.abs(participant.lateralOffsetMeters);
    if (absoluteOffset >= minorOffsetMeters) {
      if (this.state.trackLimitMode !== "disabled") participant.lapHasTrackLimitIncident = true;
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
    participant.lapHasTrackLimitIncident = true;
    participant.trackLimitWarnings = (participant.trackLimitWarnings ?? 0) + 1;
    if (this.state.trackLimitMode === "warningsOnly") {
      const investigation: InvestigationSnapshot = {
        id: crypto.randomUUID(),
        participantId: participant.id,
        offense: `疑似切弯获利：${evidence}`,
        detectedAt: now.toISOString(),
        lapNumber: Math.max(1, participant.completedLaps + 1),
        status: "pending",
        penaltyId: null,
        resolvedAt: null
      };
      (this.state.investigations ??= []).push(investigation);
      this.state.banner = {
        ...this.newBanner(
        "information", `正在调查 · ${participant.displayName}`,
        `${investigation.offense} · 第 ${investigation.lapNumber} 圈 · ${now.toLocaleTimeString("zh-CN", { hour12: false })}`,
        participant.id, 8_000, now),
        isInvestigation: true
      };
      this.recordEvent("investigationOpened",
        `${participant.displayName} 正在接受调查：${investigation.offense}（第 ${investigation.lapNumber} 圈）。`,
        participant.id, now);
      this.touch();
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
      reason, issuedAt: now.toISOString(), isServed: false, isRevoked: false,
      isAutomatic: true
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
    return participant.qualifyingEligible !== false && participant.isConnected &&
      !participant.isInPitLane && !participant.isInServiceZone &&
      participant.currentLapSeconds > .05 && participant.status !== "disqualified" &&
      participant.status !== "didNotFinish" && participant.status !== "disconnected";
  }

  private eligibleForFinalPracticeLap(participant: ParticipantState): boolean {
    return participant.isConnected && !participant.isInPitLane && !participant.isInServiceZone &&
      participant.currentLapSeconds > .05 &&
      participant.status !== "disqualified" && participant.status !== "didNotFinish" &&
      participant.status !== "disconnected";
  }

  private completePracticeIfReady(now: Date): void {
    if (this.state.phase !== "practice" || !this.state.practiceTimeExpired ||
        !this.state.practiceEndsAt ||
        this.state.participants.some(participant => participant.practiceFinalLapPending ||
          this.hasActiveDisconnectedLapRecovery(participant, now))) return;
    this.captureCurrentPracticeResults();
    this.archiveActiveResult(now, true);
    if ((this.state.practiceSessionNumber ?? 1) < (this.state.practiceSessionCount ?? 1)) {
      this.state.practiceEndsAt = null;
      for (const participant of this.state.participants.filter(candidate => candidate.isConnected)) {
        participant.practiceFinalLapPending = false;
        participant.status = "ready";
      }
      this.state.banner = this.newBanner(
        "information", `${this.practiceSessionLabel()} 已结束`,
        `等待总控开启 FP${(this.state.practiceSessionNumber ?? 1) + 1}`, null, 7_000, now);
      return;
    }
    this.state.practiceEndsAt = null;
    for (const participant of this.state.participants.filter(candidate => candidate.isConnected)) {
      participant.practiceFinalLapPending = false;
      participant.status = "ready";
    }
    this.state.banner = this.newBanner(
      "information", `${this.practiceSessionLabel()} 已结束`,
      "本节成绩已冻结，等待总控下发后续流程", null, 7_000, now);
  }

  private tryStartNextPracticeSession(now: Date): boolean {
    if (this.state.phase !== "practice" ||
        (this.state.practiceSessionCount ?? 1) <= 1 ||
        (this.state.practiceSessionNumber ?? 1) >= (this.state.practiceSessionCount ?? 1) ||
        !this.state.practiceTimeExpired ||
        this.state.practiceEndsAt !== null ||
        this.state.participants.some(participant => participant.practiceFinalLapPending)) return false;

    this.state.practiceSessionNumber = (this.state.practiceSessionNumber ?? 1) + 1;
    this.state.activeResultStageId = crypto.randomUUID();
    const sessionIndex = this.state.practiceSessionNumber - 1;
    this.state.practiceEndsAt = new Date(
      now.getTime() + (this.state.practiceSessionMinutes?.[sessionIndex] ?? 60) * 60_000).toISOString();
    this.state.practiceTimeExpired = false;
    this.state.flag = "green";
    this.state.flagMessage = null;
    this.state.receivedLapEvents = [];
    for (const participant of this.state.participants) this.resetForNextPracticeSession(participant);
    this.state.banner = this.newBanner(
      "information", `${this.practiceSessionLabel()} 开始`,
      `${this.state.practiceSessionMinutes?.[sessionIndex] ?? 60} 分钟`, null, 7_000, now);
    return true;
  }

  private configurePractice(command: SessionCommand): void {
    const sessionCount = clampInteger(command.practiceSessionCount ?? 1, 1, 3);
    this.state.practiceSessionCount = sessionCount;
    this.state.practiceSessionMinutes = Array.from({ length: sessionCount }, (_, index) =>
      clampInteger(command.practiceSessionMinutes?.[index] ?? 60, 1, 180));
  }

  private captureCurrentPracticeResults(): void {
    const sessionNumber = this.state.practiceSessionNumber ?? 0;
    if (sessionNumber < 1 || sessionNumber > 3) return;
    for (const participant of this.state.participants) {
      participant.practiceSessionBestLapSeconds ??= [null, null, null];
      participant.practiceSessionBestLapSeconds[sessionNumber - 1] = participant.bestLapSeconds ?? null;
    }
  }

  private resetForNextPracticeSession(participant: ParticipantState): void {
    this.resetForNextQualifyingSession(participant);
    participant.practiceFinalLapPending = false;
  }

  private practiceSessionLabel(): string {
    return (this.state.practiceSessionCount ?? 1) === 1
      ? "练习赛"
      : `FP${clampInteger(this.state.practiceSessionNumber ?? 1, 1, this.state.practiceSessionCount ?? 1)}`;
  }

  private completeQualifyingIfReady(now: Date): void {
    if (this.state.phase !== "qualifying" || !this.state.qualifyingTimeExpired ||
        !this.state.qualifyingEndsAt ||
        this.state.participants.some(participant => participant.qualifyingFinalLapPending ||
          this.hasActiveDisconnectedLapRecovery(participant, now))) return;
    this.captureCurrentQualifyingResults();
    this.archiveActiveResult(now, true);
    if ((this.state.qualifyingSessionNumber ?? 1) < (this.state.qualifyingSessionCount ?? 1)) {
      this.eliminateFromCurrentQualifyingSession();
      this.state.qualifyingEndsAt = null;
      this.state.flag = "green";
      this.state.flagMessage = null;
      for (const participant of this.state.participants.filter(candidate => candidate.isConnected)) {
        participant.qualifyingFinalLapPending = false;
        participant.status = "ready";
      }
      this.state.banner = this.newBanner(
        "information", `${this.qualifyingSessionLabel()} 已结束`,
        `本节淘汰 ${this.state.qualifyingEliminationCounts?.[(this.state.qualifyingSessionNumber ?? 1) - 1] ?? 0} 人 · 等待总控开启 Q${(this.state.qualifyingSessionNumber ?? 1) + 1}`,
        null, 7_000, now);
      return;
    }
    this.state.phase = "grid";
    this.state.activeResultStageId = null;
    this.state.flag = "green";
    this.state.flagMessage = null;
    this.state.qualifyingEndsAt = null;
    this.state.qualifyingTimeExpired = false;
    this.state.qualifyingSessionNumber = 0;
    for (const participant of this.state.participants.filter(candidate => candidate.isConnected))
      participant.status = "ready";
    this.state.banner ??= this.newBanner("chequeredFlag", "排位赛结束", "成绩已冻结", null, 8_000, now);
  }

  private tryStartNextQualifyingSession(now: Date): boolean {
    if (this.state.phase !== "qualifying" ||
        (this.state.qualifyingSessionCount ?? 1) <= 1 ||
        (this.state.qualifyingSessionNumber ?? 1) >= (this.state.qualifyingSessionCount ?? 1) ||
        !this.state.qualifyingTimeExpired ||
        this.state.qualifyingEndsAt !== null ||
        this.state.participants.some(participant => participant.qualifyingFinalLapPending)) return false;

    this.state.qualifyingSessionNumber = (this.state.qualifyingSessionNumber ?? 1) + 1;
    this.state.activeResultStageId = crypto.randomUUID();
    const sessionIndex = this.state.qualifyingSessionNumber - 1;
    this.state.qualifyingEndsAt = new Date(
      now.getTime() + (this.state.qualifyingSessionMinutes?.[sessionIndex] ?? 10) * 60_000).toISOString();
    this.state.qualifyingTimeExpired = false;
    this.state.flag = "green";
    this.state.flagMessage = null;
    this.state.receivedLapEvents = [];
    for (const participant of this.state.participants.filter(candidate => candidate.qualifyingEligible !== false))
      this.resetForNextQualifyingSession(participant);
    const eliminationText = this.state.qualifyingSessionNumber < (this.state.qualifyingSessionCount ?? 1)
      ? ` · 本节淘汰 ${this.state.qualifyingEliminationCounts?.[sessionIndex] ?? 0} 人`
      : "";
    this.state.banner = this.newBanner(
      "information", `${this.qualifyingSessionLabel()} 开始`,
      `${this.state.qualifyingSessionMinutes?.[sessionIndex] ?? 10} 分钟${eliminationText}`,
      null, 7_000, now);
    return true;
  }

  private configureQualifying(command: SessionCommand): void {
    const sessionCount = clampInteger(command.qualifyingSessionCount ?? 1, 1, 3);
    this.state.qualifyingSessionCount = sessionCount;
    if (sessionCount === 1) {
      this.state.qualifyingSessionMinutes = [clampInteger(command.qualifyingMinutes ?? 10, 1, 180)];
      this.state.qualifyingEliminationCounts = [];
      return;
    }
    const defaults = sessionCount === 2 ? [15, 12] : [18, 15, 12];
    this.state.qualifyingSessionMinutes = Array.from({ length: sessionCount }, (_, index) =>
      clampInteger(command.qualifyingSessionMinutes?.[index] ?? defaults[index], 1, 180));
    const eligibleCount = this.state.participants.filter(candidate => candidate.isConnected &&
      candidate.status !== "disqualified").length;
    const defaultEliminations = defaultQualifyingEliminations(eligibleCount, sessionCount);
    let remaining = eligibleCount;
    this.state.qualifyingEliminationCounts = Array.from({ length: sessionCount - 1 }, (_, index) => {
      const requested = command.qualifyingEliminationCounts?.[index];
      const value = requested === null || requested === undefined
        ? defaultEliminations[index]
        : clampInteger(requested, 0, 12);
      const resolved = clampInteger(value, 0, Math.max(0, remaining - 1));
      remaining -= resolved;
      return resolved;
    });
  }

  private captureCurrentQualifyingResults(): void {
    const sessionNumber = this.state.qualifyingSessionNumber ?? 0;
    if (sessionNumber < 1 || sessionNumber > 3) return;
    for (const participant of this.state.participants.filter(candidate => candidate.qualifyingEligible !== false)) {
      participant.qualifyingSessionBestLapSeconds ??= [null, null, null];
      participant.qualifyingSessionBestLapSeconds[sessionNumber - 1] = participant.bestLapSeconds ?? null;
    }
  }

  private eliminateFromCurrentQualifyingSession(): void {
    const sessionNumber = this.state.qualifyingSessionNumber ?? 1;
    const sessionCount = this.state.qualifyingSessionCount ?? 1;
    if (sessionNumber < 1 || sessionNumber >= sessionCount) return;
    const eliminate = this.state.qualifyingEliminationCounts?.[sessionNumber - 1] ?? 0;
    if (eliminate <= 0) return;
    const candidates = this.state.participants
      .filter(candidate => candidate.qualifyingEligible !== false)
      .sort((left, right) => compareOptionalTimes(left.bestLapSeconds, right.bestLapSeconds) ||
        Date.parse(left.joinedAt) - Date.parse(right.joinedAt));
    const count = Math.min(eliminate, Math.max(0, candidates.length - 1));
    for (const participant of candidates.slice(candidates.length - count)) {
      participant.qualifyingEligible = false;
      participant.qualifyingEliminatedInSession = sessionNumber;
      participant.qualifyingFinalLapPending = false;
      participant.status = participant.isConnected ? "ready" : "disconnected";
      participant.automaticYellowActive = false;
      participant.hazardCandidateStartedAt = null;
      participant.hazardRecoveryStartedAt = null;
    }
  }

  private resetForNextQualifyingSession(participant: ParticipantState): void {
    participant.completedLaps = 0;
    participant.currentSector = 0;
    participant.trackProgress = 0;
    participant.currentLapSeconds = 0;
    participant.lastLapSeconds = null;
    participant.bestLapSeconds = null;
    participant.bestSectorSeconds = [];
    participant.bestLapSectorSeconds = [];
    participant.lastLapCompletedAt = null;
    participant.disconnectedLapRecoveryUntil = null;
    participant.qualifyingFinalLapPending = false;
    participant.lapHasTrackLimitIncident = false;
    participant.progressContinuityReady = false;
    participant.lastTelemetryMonotonicMilliseconds = 0;
    participant.lastContinuityProgress = 0;
    participant.shortcutPenaltyIssued = false;
    participant.status = participant.isConnected ? "onTrack" : "disconnected";
  }

  private qualifyingSessionLabel(): string {
    return (this.state.qualifyingSessionCount ?? 1) === 1
      ? "排位赛"
      : `Q${clampInteger(this.state.qualifyingSessionNumber ?? 1, 1, this.state.qualifyingSessionCount ?? 1)}`;
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
    this.state.qualifyingSessionNumber = 0;
    this.state.qualifyingSessionCount = 1;
    this.state.qualifyingSessionMinutes = [10];
    this.state.qualifyingEliminationCounts = [];
    this.state.practiceEndsAt = null;
    this.state.practiceTimeExpired = false;
    this.state.practiceSessionNumber = 0;
    this.state.practiceSessionCount = 1;
    this.state.practiceSessionMinutes = [60];
    this.state.banner = null;
    this.state.receivedLapEvents = [];
    this.liveProgressSamples.clear();
    this.liveProgressTrackers.clear();
    for (const participant of this.state.participants) this.resetParticipant(participant, true);
  }

  private resetCompetitiveState(): void {
    this.clearYellowState();
    this.state.chequeredImminent = false;
    this.state.penalties = [];
    this.state.investigations = [];
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
    this.state.practiceEndsAt = null;
    this.state.practiceTimeExpired = false;
    this.state.practiceSessionNumber = 0;
    this.state.practiceSessionCount = 1;
    this.state.practiceSessionMinutes = [60];
    this.state.banner = null;
    this.liveProgressSamples.clear();
    this.liveProgressTrackers.clear();
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
    participant.disconnectedLapRecoveryUntil = null;
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
    participant.qualifyingEligible = true;
    participant.qualifyingEliminatedInSession = null;
    participant.qualifyingSessionBestLapSeconds = [null, null, null];
    participant.practiceFinalLapPending = false;
    participant.practiceSessionBestLapSeconds = [null, null, null];
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
    participant.telemetryValid = false;
    participant.hasWorldPosition = false;
    participant.lastTelemetryReceivedAt = null;
    participant.isApproachingPit = false;
    participant.isOnPitRoute = false;
    participant.lastProcessedImpactSequence = participant.lastReportedImpactSequence ?? participant.lastProcessedImpactSequence ?? 0;
    participant.lastImpactAt = null;
    participant.lastImpactMagnitudeMps = 0;
    participant.lastImpactSpeedLossMps = 0;
    participant.lastImpactSmashableVelDiff = 0;
    participant.lastImpactSmashableMass = 0;
    this.collisionTrajectories.delete(participant.id);
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
      .filter(candidate => candidate.reservationActive !== false)
      .filter(candidate => this.state.phase !== "qualifying" || candidate.qualifyingEligible !== false)
      .filter(candidate => candidate.bestLapSeconds !== null && candidate.bestLapSeconds !== undefined)
      .sort((left, right) => (left.bestLapSeconds! - right.bestLapSeconds!) ||
        left.joinedAt.localeCompare(right.joinedAt))[0];
    return participant ? { participant, time: participant.bestLapSeconds! } : null;
  }

  private fastestSectors(): Array<number | null> {
    const activeParticipants = this.state.participants.filter(participant => participant.reservationActive !== false);
    const count = activeParticipants.reduce(
      (maximum, participant) => Math.max(maximum, participant.bestSectorSeconds.length), 0);
    return Array.from({ length: count }, (_, index) => {
      const values = activeParticipants
        .filter(participant => this.state.phase !== "qualifying" || participant.qualifyingEligible !== false)
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
    if (this.requiresPostRaceAdjustment(participant) ||
        this.state.phase === "race" && this.state.totalRaceLaps - participant.completedLaps <= 3) {
      return {
        id: crypto.randomUUID(), participantId: participant.id, kind: "time",
        valueSeconds: 20, gridPlaces: null,
        reason: this.requiresPostRaceAdjustment(participant)
          ? `赛后下发的通过维修区处罚，按等效规则改为完赛加时：${reason}`
          : `最后三圈下发的通过维修区处罚，按等效规则改为完赛加时：${reason}`,
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

  private finalizePendingPenaltiesAtFinish(participant: ParticipantState, now: Date): void {
    this.resetLivePenaltyServiceState(participant);
    for (const penalty of this.state.penalties) {
      if (penalty.participantId === participant.id && !penalty.isRevoked && !penalty.isServed &&
          penalty.kind === "time" && !penalty.isPostRaceAdjustment)
        penalty.isPostRaceAdjustment = true;
    }
    if (this.hasPendingDriveThrough(participant.id))
      this.convertDriveThroughToTimeAdjustment(
        participant, now, "车手已经接收方格旗，改按等效完赛加时结算", false);
    const pendingStopAndGo = this.state.penalties.filter(penalty =>
      penalty.participantId === participant.id && !penalty.isRevoked && !penalty.isServed &&
      penalty.kind === "stopAndGo");
    if (pendingStopAndGo.length === 0) return;
    this.markPendingPenaltiesServed(participant.id, "stopAndGo");
    const equivalentSeconds = pendingStopAndGo.reduce((sum, penalty) => sum + (penalty.valueSeconds ?? 0), 20);
    this.state.penalties.push({
      id: crypto.randomUUID(), participantId: participant.id, kind: "time",
      valueSeconds: equivalentSeconds, gridPlaces: null,
      reason: "未执行的停车并通过维修区处罚，按维修区通行 20 秒加原停车时间计入完赛成绩",
      issuedAt: now.toISOString(), isServed: false, isRevoked: false,
      isPostRaceAdjustment: true,
      isAutomatic: pendingStopAndGo.some(penalty => penalty.isAutomatic)
    });
    this.recordEvent("stopAndGoPostRaceAdjustment",
      `${participant.displayName} 的未执行停车并通过维修区处罚已折算为 +${equivalentSeconds.toFixed(0)} 秒完赛加时。`,
      participant.id, now);
  }

  private enforceMinimumPitStopsAtFinish(participant: ParticipantState, now: Date): void {
    const required = clampInteger(this.state.minimumRequiredPitStops ?? 1, 0, 20);
    if (required === 0 || participant.completedPitServices >= required) return;
    const reason =
      `未完成规定的最少有效维修停留次数（${participant.completedPitServices}/${required}）。`;
    this.state.penalties.push({
      id: crypto.randomUUID(), participantId: participant.id, kind: "disqualification",
      valueSeconds: null, gridPlaces: null, reason,
      issuedAt: now.toISOString(), isServed: false, isRevoked: false,
      isPostRaceAdjustment: true, isAutomatic: true
    });
    participant.status = "disqualified";
    this.recordEvent(
      "minimumPitStopsNotMet",
      `${participant.displayName} 完赛时只完成 ${participant.completedPitServices}/${required} 次有效维修停留，判定未满足完赛条件。`,
      participant.id,
      now);
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
    reason: string,
    announce = true): void {
    if (!this.hasPendingDriveThrough(participant.id)) return;
    const wasAutomatic = this.state.penalties.some(penalty => penalty.participantId === participant.id &&
      penalty.kind === "driveThrough" && !penalty.isRevoked && !penalty.isServed && penalty.isAutomatic);
    this.markPendingPenaltiesServed(participant.id, "driveThrough");
    this.state.penalties.push({
      id: crypto.randomUUID(), participantId: participant.id, kind: "time",
      valueSeconds: 20, gridPlaces: null,
      reason: `通过维修区处罚未按期执行：${reason}`,
      issuedAt: now.toISOString(), isServed: false, isRevoked: false,
      isPostRaceAdjustment: true,
      isAutomatic: wasAutomatic
    });
    participant.driveThroughOverdue = true;
    participant.driveThroughReminderAt = now.toISOString();
    participant.driveThroughVisitActive = false;
    participant.driveThroughStopCandidateStartedAt = null;
    if (announce)
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
    const wasAutomatic = this.state.penalties.some(penalty => penalty.participantId === participant.id &&
      penalty.kind === "time" && !penalty.isRevoked && !penalty.isServed && penalty.isAutomatic);
    this.markPendingPenaltiesServed(participant.id, "time");
    let replacement: PenaltySnapshot | null = null;
    if (!this.hasPendingDriveThrough(participant.id)) {
      replacement = {
        ...this.createDriveThroughPenalty(participant, `停车罚时执行失败：${reason}`, now),
        isAutomatic: wasAutomatic
      };
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
    if (!this.isRaceClassificationPhase()) return null;
    if (reference.status === "finished" && participant.status === "finished")
      return this.adjustedRaceTotalSeconds(participant, now) - this.adjustedRaceTotalSeconds(reference, now);
    const liveDelta = this.liveRaceDeltaSeconds(reference, participant);
    if (liveDelta !== null) return liveDelta;
    if (reference.completedLaps !== participant.completedLaps) return null;
    if (reference.lastLapCompletedAt && participant.lastLapCompletedAt)
      return (Date.parse(participant.lastLapCompletedAt) - Date.parse(reference.lastLapCompletedAt)) / 1_000;
    return null;
  }

  private recordRaceProgressSample(
    participant: ParticipantState,
    now: Date,
    isPitRoute: boolean): void {
    const tracker = this.liveProgressTrackers.get(participant.id) ?? {
      lastProgress: 0,
      lapOffset: 0,
      ready: false
    };
    if (isPitRoute) {
      tracker.ready = false;
      this.liveProgressTrackers.set(participant.id, tracker);
      return;
    }

    const progress = clamp(participant.trackProgress, 0, 1);
    if (tracker.ready && progress < tracker.lastProgress - .75) tracker.lapOffset++;
    tracker.lastProgress = progress;
    tracker.ready = true;
    this.liveProgressTrackers.set(participant.id, tracker);
    const distanceLaps = tracker.lapOffset + progress;
    if (!Number.isFinite(distanceLaps) || distanceLaps < 0) return;
    const elapsedSeconds = this.raceElapsedSeconds(now);
    this.appendRaceProgressSample(participant.id, distanceLaps, elapsedSeconds);
  }

  private reconcileRaceProgressAtCompletedLap(participant: ParticipantState, now: Date): void {
    const tracker = this.liveProgressTrackers.get(participant.id) ?? {
      lastProgress: 0,
      lapOffset: 0,
      ready: false
    };
    const crossingAlreadyObserved = tracker.lastProgress <= .25;
    const eventOffset = tracker.lapOffset + (crossingAlreadyObserved ? 0 : 1);
    tracker.lapOffset = Math.max(eventOffset, participant.completedLaps);
    const finishDistance = tracker.lapOffset;
    tracker.lastProgress = 0;
    tracker.ready = false;
    this.liveProgressTrackers.set(participant.id, tracker);
    this.appendRaceProgressSample(participant.id, finishDistance, this.raceElapsedSeconds(now));
  }

  private appendRaceProgressSample(participantId: string, distanceLaps: number, elapsedSeconds: number): void {
    const samples = this.liveProgressSamples.get(participantId) ?? [];
    const last = samples.at(-1);
    if (last) {
      if (distanceLaps < last.distanceLaps - RaceCore.liveGapProgressJitter) return;
      // Keep the first passage time. Replacing it for every sub-jitter forward
      // sample collapses a whole lap into one moving point and stretches Delta.
      if (distanceLaps <= last.distanceLaps) return;
    }

    samples.push({ distanceLaps, elapsedSeconds });
    const minimumDistance = distanceLaps - RaceCore.liveGapHistoryLaps;
    let removeCount = 0;
    while (removeCount < samples.length - 2 && samples[removeCount].distanceLaps < minimumDistance)
      removeCount++;
    if (removeCount > 0) samples.splice(0, removeCount);
    if (samples.length > RaceCore.maximumLiveGapSamples)
      samples.splice(0, samples.length - RaceCore.maximumLiveGapSamples);
    this.liveProgressSamples.set(participantId, samples);
  }

  private static estimatePassageTime(samples: RaceProgressSample[], distanceLaps: number): number | null {
    if (samples.length === 0 ||
        distanceLaps < samples[0].distanceLaps - RaceCore.liveGapProgressJitter ||
        distanceLaps > samples[samples.length - 1].distanceLaps + RaceCore.liveGapProgressJitter)
      return null;
    let lower = 0, upper = samples.length - 1;
    while (lower < upper) {
      const middle = lower + Math.floor((upper - lower) / 2);
      if (samples[middle].distanceLaps < distanceLaps) lower = middle + 1;
      else upper = middle;
    }
    const next = samples[lower];
    if (Math.abs(next.distanceLaps - distanceLaps) <= 1e-9)
      return next.elapsedSeconds;
    if (lower === 0) return null;
    const previous = samples[lower - 1];
    const span = next.distanceLaps - previous.distanceLaps;
    if (span <= 0) return previous.elapsedSeconds;
    const fraction = clamp((distanceLaps - previous.distanceLaps) / span, 0, 1);
    return previous.elapsedSeconds + (next.elapsedSeconds - previous.elapsedSeconds) * fraction;
  }

  private liveRaceDeltaSeconds(reference: ParticipantState, participant: ParticipantState): number | null {
    const referenceSamples = this.liveProgressSamples.get(reference.id);
    const participantSamples = this.liveProgressSamples.get(participant.id);
    if (!referenceSamples?.length || !participantSamples?.length) return null;
    const referenceDistance = referenceSamples[referenceSamples.length - 1].distanceLaps;
    const participantDistance = participantSamples[participantSamples.length - 1].distanceLaps;
    if (referenceDistance - participantDistance >= RaceCore.maximumLiveGapDistanceLaps) return null;
    const commonDistance = Math.min(referenceDistance, participantDistance);
    if (commonDistance < Math.max(referenceSamples[0].distanceLaps, participantSamples[0].distanceLaps))
      return null;
    const referenceTime = RaceCore.estimatePassageTime(referenceSamples, commonDistance);
    const participantTime = RaceCore.estimatePassageTime(participantSamples, commonDistance);
    if (referenceTime === null || participantTime === null) return null;
    return Math.max(0, participantTime - referenceTime);
  }

  private orderParticipants(now: Date): ParticipantState[] {
    const participants = this.state.participants.filter(participant => participant.reservationActive !== false);
    if ((this.state.phase === "qualifying" || this.state.phase === "grid") &&
        (this.state.qualifyingSessionCount ?? 1) > 1) {
      const count = this.state.qualifyingSessionCount ?? 1;
      return participants.sort((left, right) => {
        const leftGroup = left.qualifyingEliminatedInSession
          ? count - left.qualifyingEliminatedInSession : 0;
        const rightGroup = right.qualifyingEliminatedInSession
          ? count - right.qualifyingEliminatedInSession : 0;
        return leftGroup - rightGroup ||
          compareOptionalTimes(this.qualifyingDisplayedBestLap(left), this.qualifyingDisplayedBestLap(right)) ||
          left.joinedAt.localeCompare(right.joinedAt);
      });
    }
    if (this.state.phase === "practice" || this.state.phase === "qualifying" || this.state.phase === "grid") {
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

  private qualifyingDisplayedBestLap(participant: ParticipantState): number | null {
    if ((this.state.qualifyingSessionCount ?? 1) <= 1 ||
        (this.state.phase !== "qualifying" && this.state.phase !== "grid"))
      return participant.bestLapSeconds ?? null;
    const eliminatedIn = participant.qualifyingEliminatedInSession;
    if (eliminatedIn)
      return participant.qualifyingSessionBestLapSeconds?.[eliminatedIn - 1] ?? null;
    const currentIndex = clampInteger((this.state.qualifyingSessionNumber ?? 1) - 1, 0, 2);
    return participant.bestLapSeconds ?? participant.qualifyingSessionBestLapSeconds?.[currentIndex] ?? null;
  }

  private isRaceClassificationPhase(): boolean {
    return this.state.phase === "race" || this.state.phase === "finished" ||
      this.state.phase === "suspended" && this.state.phaseBeforeSuspension === "race";
  }

  private hasActiveDisconnectedLapRecovery(participant: ParticipantState, now: Date): boolean {
    return Boolean(participant.disconnectedLapRecoveryUntil) &&
      Date.parse(participant.disconnectedLapRecoveryUntil!) > now.getTime();
  }

  private expireDisconnectedLapRecoveries(now: Date): boolean {
    let changed = false;
    for (const participant of this.state.participants) {
      if (!participant.disconnectedLapRecoveryUntil ||
          Date.parse(participant.disconnectedLapRecoveryUntil) > now.getTime()) continue;
      participant.disconnectedLapRecoveryUntil = null;
      changed = true;
      if (participant.isConnected) continue;
      participant.qualifyingFinalLapPending = false;
      participant.practiceFinalLapPending = false;
      const effectivePhase = this.state.phase === "suspended"
        ? this.state.phaseBeforeSuspension
        : this.state.phase;
      if (effectivePhase === "race" && this.state.flag === "chequered" && participant.status === "disconnected") {
        participant.status = "didNotFinish";
        participant.finishedAt ??= now.toISOString();
      }
    }
    if (changed) {
      this.completeQualifyingIfReady(now);
      this.completePracticeIfReady(now);
      this.tryCompleteRaceIfReady(now);
    }
    return changed;
  }

  private tryCompleteRaceIfReady(now: Date): boolean {
    if (this.state.phase !== "race" || this.state.flag !== "chequered") return false;
    const awaitingFinish = this.state.participants.some(participant =>
      participant.reservationActive !== false &&
      ((participant.isConnected && !terminal(participant.status) && participant.status !== "disconnected") ||
       this.hasActiveDisconnectedLapRecovery(participant, now)));
    if (awaitingFinish) return false;
    this.state.phase = "finished";
    this.state.raceEndedAt = now.toISOString();
    const winner = this.orderParticipants(now).find(participant =>
      participant.reservationActive !== false && participant.status === "finished");
    if (winner) this.state.banner = this.newBanner(
      "winner",
      "比赛胜者",
      `${winner.displayName}  ${formatRaceTime(this.adjustedRaceTotalSeconds(winner, now))}`,
      winner.id,
      null,
      now);
    this.archiveActiveResult(now, true);
    return true;
  }

  private currentResultIsComplete(): boolean {
    return this.state.phase === "finished" ||
      this.state.phase === "practice" && this.state.practiceTimeExpired === true &&
        this.state.practiceEndsAt === null &&
        this.state.participants.every(participant => !participant.practiceFinalLapPending) ||
      this.state.phase === "qualifying" && this.state.qualifyingTimeExpired === true &&
        this.state.participants.every(participant => !participant.qualifyingFinalLapPending);
  }

  private archiveActiveResult(now: Date, isComplete: boolean): void {
    const id = this.state.activeResultStageId;
    if (!id) return;
    const activePhase = this.state.phase === "finished"
      ? "race"
      : this.state.phase === "suspended"
        ? this.state.phaseBeforeSuspension
        : this.state.phase;
    if (activePhase !== "practice" && activePhase !== "qualifying" && activePhase !== "race") return;
    const snapshot = this.snapshot(now);
    const sessionNumber = activePhase === "practice"
      ? Math.max(1, this.state.practiceSessionNumber ?? 1)
      : activePhase === "qualifying"
        ? Math.max(1, this.state.qualifyingSessionNumber ?? 1)
        : 1;
    const sessionCount = activePhase === "practice"
      ? Math.max(1, this.state.practiceSessionCount ?? 1)
      : activePhase === "qualifying"
        ? Math.max(1, this.state.qualifyingSessionCount ?? 1)
        : 1;
    const label = activePhase === "practice"
      ? sessionCount > 1 ? `FP${sessionNumber}` : "练习赛"
      : activePhase === "qualifying"
        ? sessionCount > 1 ? `Q${sessionNumber}` : "排位赛"
        : "正赛";
    const archived: StageResultSnapshot = {
      id,
      phase: activePhase,
      label,
      sessionNumber,
      sessionCount,
      isComplete,
      completedAt: now.toISOString(),
      sessionName: this.state.sessionName,
      trackName: this.state.trackName,
      fastestParticipantId: snapshot.fastestParticipantId,
      fastestLapSeconds: snapshot.fastestLapSeconds,
      participants: snapshot.participants.map(participant => ({
        id: participant.id,
        position: participant.position,
        displayName: participant.displayName,
        themeColor: participant.themeColor,
        teamName: participant.teamName,
        teamColor: participant.teamColor,
        status: participant.status,
        completedLaps: participant.completedLaps,
        trackProgress: participant.trackProgress,
        bestLapSeconds: participant.bestLapSeconds,
        raceTotalSeconds: participant.raceTotalSeconds,
        adjustedRaceTotalSeconds: participant.adjustedRaceTotalSeconds,
        gapToLeaderSeconds: participant.gapToLeaderSeconds,
        timePenaltySeconds: participant.timePenaltySeconds ?? 0,
        penalties: participant.penalties.map(penalty => ({ ...penalty }))
      }))
    };
    this.state.resultHistory ??= [];
    const existingIndex = this.state.resultHistory.findIndex(result => result.id === id);
    if (existingIndex >= 0) this.state.resultHistory[existingIndex] = archived;
    else this.state.resultHistory.push(archived);
    if (this.state.resultHistory.length > 24)
      this.state.resultHistory.splice(0, this.state.resultHistory.length - 24);
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
