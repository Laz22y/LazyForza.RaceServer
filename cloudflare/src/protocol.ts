export const protocolVersion = 2;
export const maximumMessageBytes = 64 * 1024;

export type SessionPhase = "lobby" | "qualifying" | "grid" | "outLap" | "formationLap" | "countdown" | "race" | "suspended" | "finished";
export type ControlFlag = "green" | "yellow" | "red" | "chequered";
export type ParticipantStatus = "connected" | "ready" | "onTrack" | "inPitLane" | "inService" | "finished" | "didNotFinish" | "disqualified" | "disconnected";
export type GripCondition = "unknown" | "slightlyReduced" | "moderatelyReduced" | "severelyReduced" | "atLimit";
export type PenaltyKind = "warning" | "time" | "driveThrough" | "stopAndGo" | "gridDrop" | "disqualification";
export type BannerKind = "information" | "fastestLap" | "penalty" | "yellowFlag" | "redFlag" | "blueFlag" | "chequeredFlag" | "winner";
export type TrackLimitMode = "warningsOnly" | "automatic" | "disabled";

export interface RaceEnvelope<T = unknown> {
  protocolVersion: number;
  type: string;
  sequence: number;
  payload: T;
}

export interface LoginRequest {
  password: string;
  displayName: string;
  themeColor: string;
  teamName?: string | null;
  clientVersion: string;
  resumeToken?: string | null;
  trackId?: string | null;
  trackRevision?: string | null;
  trackPackageHash?: string | null;
  sectorCount?: number | null;
  teamId?: string | null;
}

export interface TeamDefinition { id: string; name: string; themeColor: string; }

export interface ReadyUpdate { isReady: boolean; }

export interface TelemetryUpdate {
  clientMonotonicMilliseconds: number;
  trackProgress: number;
  lateralOffsetMeters: number;
  mapX: number;
  mapY: number;
  speedKph: number;
  completedLaps: number;
  currentSector: number;
  currentLapSeconds: number;
  isInPitLane: boolean;
  isInServiceZone: boolean;
  isTelemetryValid: boolean;
  isPausedOrRewinding: boolean;
  gripCondition: GripCondition;
  pitServiceElapsedSeconds: number;
  pitServiceRequirementMet: boolean;
  completedPitServices: number;
  trackToleranceMeters?: number;
  trackLengthMeters?: number;
  pitSpeedLimitKph?: number;
  pitLaneElapsedSeconds?: number;
  isApproachingPit?: boolean;
}

export interface LapCompleted {
  eventId: string;
  lapNumber: number;
  lapSeconds: number;
  sectorSeconds: number[];
  isValid: boolean;
  invalidReason?: string | null;
  clientMonotonicMilliseconds: number;
  isBestLapEligible?: boolean;
}

export interface PenaltySnapshot {
  id: string;
  participantId: string;
  kind: PenaltyKind;
  valueSeconds?: number | null;
  gridPlaces?: number | null;
  reason: string;
  issuedAt: string;
  isServed: boolean;
  isRevoked: boolean;
  isPostRaceAdjustment?: boolean;
}

export interface ParticipantSnapshot {
  id: string;
  position: number;
  displayName: string;
  themeColor: string;
  teamName?: string | null;
  status: ParticipantStatus;
  isConnected: boolean;
  isReady: boolean;
  completedLaps: number;
  currentSector: number;
  trackProgress: number;
  mapX: number;
  mapY: number;
  speedKph: number;
  currentLapSeconds: number;
  lastLapSeconds?: number | null;
  bestLapSeconds?: number | null;
  gapToLeaderSeconds?: number | null;
  intervalSeconds?: number | null;
  isInPitLane: boolean;
  isInServiceZone: boolean;
  pitServiceElapsedSeconds: number;
  pitServiceRequirementMet: boolean;
  completedPitServices: number;
  gripCondition: GripCondition;
  bestSectorSeconds: Array<number | null>;
  penalties: PenaltySnapshot[];
  lastSeenAt: string;
  qualifyingFinalLapPending: boolean;
  raceTotalSeconds?: number | null;
  adjustedRaceTotalSeconds?: number | null;
  timePenaltySeconds?: number;
  trackLimitWarnings?: number;
  teamId?: string | null;
  teamColor?: string | null;
  pitLaneElapsedSeconds?: number;
  pendingTimePenaltySeconds?: number;
  isServingTimePenalty?: boolean;
  penaltyServiceElapsedSeconds?: number;
  penaltyServiceRequiredSeconds?: number;
  hasPendingDriveThrough?: boolean;
  penaltyServiceCompleted?: boolean;
  driveThroughLapsRemaining?: number | null;
  driveThroughReminderAt?: string | null;
  driveThroughOverdue?: boolean;
  isServingDriveThrough?: boolean;
}

export interface BannerSnapshot {
  id: string;
  kind: BannerKind;
  title: string;
  detail?: string | null;
  participantId?: string | null;
  createdAt: string;
  expiresAt?: string | null;
}

export interface SessionSnapshot {
  revision: number;
  sessionName: string;
  phase: SessionPhase;
  flag: ControlFlag;
  flagMessage?: string | null;
  trackId?: string | null;
  trackRevision?: string | null;
  trackPackageHash?: string | null;
  totalRaceLaps: number;
  startsAt?: string | null;
  startSequenceAt?: string | null;
  illuminatedStartLights: number;
  startLightsOut: boolean;
  qualifyingEndsAt?: string | null;
  qualifyingTimeExpired: boolean;
  fastestParticipantId?: string | null;
  fastestLapSeconds?: number | null;
  fastestSectorSeconds: Array<number | null>;
  fastestLapSectorSeconds?: Array<number | null>;
  banner?: BannerSnapshot | null;
  participants: ParticipantSnapshot[];
  serverTime: string;
  yellowZones: YellowZoneSnapshot[];
  sectorCount: number;
  allowTeams: boolean;
  trackName?: string | null;
  blueFlags: BlueFlagSnapshot[];
  raceElapsedSeconds?: number | null;
  suspendedFromPhase?: SessionPhase | null;
  driversPerTeam: number;
  teams: TeamDefinition[];
  chequeredImminent: boolean;
}

export interface YellowZoneSnapshot {
  sectorIndex?: number | null;
  isAutomatic: boolean;
  reason: string;
  participantId?: string | null;
  participantName?: string | null;
}
export interface BlueFlagSnapshot { recipientParticipantId: string; approachingParticipantId: string; distanceAhead: number; }

export interface SessionCommand {
  phase: SessionPhase;
  sessionName?: string | null;
  totalRaceLaps?: number | null;
  countdownSeconds?: number | null;
  qualifyingMinutes?: number | null;
}

export interface FlagCommand { flag: ControlFlag; message?: string | null; sectorIndex?: number | null; }
export interface RoomSettings {
  sessionName: string;
  totalRaceLaps: number;
  sectorCount: number;
  automaticYellowEnabled: boolean;
  slowSpeedKph: number;
  slowDurationSeconds: number;
  severeLateralOffsetMeters: number;
  recoveryDurationSeconds: number;
  allowTeams?: boolean;
  trackName?: string | null;
  trackId?: string | null;
  trackRevision?: string | null;
  trackPackageHash?: string | null;
  teamCount?: number;
  driversPerTeam?: number;
  teams?: TeamDefinition[];
  trackLimitMode?: TrackLimitMode;
}
export interface RaceEventSnapshot {
  sequence: number;
  occurredAt: string;
  type: string;
  message: string;
  participantId?: string | null;
}
export interface PenaltyCommand {
  participantId: string;
  kind: PenaltyKind;
  valueSeconds?: number | null;
  gridPlaces?: number | null;
  reason: string;
}
export interface ParticipantCommand { participantId: string; status: ParticipantStatus; reason: string; }

export function clamp(value: unknown, minimum: number, maximum: number): number {
  const numeric = typeof value === "number" && Number.isFinite(value) ? value : minimum;
  return Math.min(maximum, Math.max(minimum, numeric));
}

export function clampInteger(value: unknown, minimum: number, maximum: number): number {
  return Math.round(clamp(value, minimum, maximum));
}

export function cleanText(value: unknown, maximumLength: number): string | null {
  if (typeof value !== "string") return null;
  const cleaned = [...value.trim()].filter(character => character >= " " && character !== "\u007f").join("");
  return cleaned.length === 0 ? null : cleaned.slice(0, maximumLength);
}

export function isThemeColor(value: unknown): value is string {
  return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value);
}
