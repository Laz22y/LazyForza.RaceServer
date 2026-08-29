import type {
  PenaltySnapshot,
  SessionSnapshot,
  StageResultSnapshot
} from "./protocol";

export interface StoredPublicTimingAccess {
  hash: string;
  generatedAt: string;
}

export interface PublicTimingAccessStatus {
  enabled: boolean;
  generatedAt: string | null;
}

export async function createPublicTimingAccess(
  now = new Date()): Promise<{ token: string; stored: StoredPublicTimingAccess }> {
  const token = base64Url(crypto.getRandomValues(new Uint8Array(32)));
  return {
    token,
    stored: { hash: await tokenDigest(token), generatedAt: now.toISOString() }
  };
}

export function normalizePublicTimingAccess(value: unknown): StoredPublicTimingAccess | null {
  if (!value || typeof value !== "object") return null;
  const candidate = value as Partial<StoredPublicTimingAccess>;
  if (typeof candidate.hash !== "string" || !/^[A-Za-z0-9_-]{43}$/.test(candidate.hash) ||
      typeof candidate.generatedAt !== "string" || !Number.isFinite(Date.parse(candidate.generatedAt)))
    return null;
  return { hash: candidate.hash, generatedAt: new Date(candidate.generatedAt).toISOString() };
}

export function publicTimingAccessStatus(
  access: StoredPublicTimingAccess | null | undefined): PublicTimingAccessStatus {
  const normalized = normalizePublicTimingAccess(access);
  return { enabled: normalized !== null, generatedAt: normalized?.generatedAt ?? null };
}

export async function verifyPublicTimingToken(
  token: string | null | undefined,
  access: StoredPublicTimingAccess | null | undefined): Promise<boolean> {
  const normalized = normalizePublicTimingAccess(access);
  if (!normalized || !token || token.length > 128) return false;
  return constantTimeEquals(await tokenDigest(token), normalized.hash);
}

export function bearerToken(request: Request): string | null {
  const authorization = request.headers.get("Authorization") ?? "";
  return authorization.toLowerCase().startsWith("bearer ")
    ? authorization.slice(7).trim() || null
    : null;
}

export function publicTimingPayload(state: SessionSnapshot, results: StageResultSnapshot[]) {
  const participantNames = new Map(state.participants.map(item => [item.id, item.displayName]));
  return {
    state: {
      revision: state.revision,
      sessionName: state.sessionName,
      phase: state.phase,
      suspendedFromPhase: state.suspendedFromPhase ?? null,
      flag: state.flag,
      flagMessage: state.flagMessage ?? null,
      trackName: state.trackName ?? null,
      totalRaceLaps: state.totalRaceLaps,
      startsAt: state.startsAt ?? null,
      practiceEndsAt: state.practiceEndsAt ?? null,
      qualifyingEndsAt: state.qualifyingEndsAt ?? null,
      raceElapsedSeconds: state.raceElapsedSeconds ?? null,
      fastestDriverName: state.fastestParticipantId
        ? participantNames.get(state.fastestParticipantId) ?? null
        : null,
      fastestLapSeconds: state.fastestLapSeconds ?? null,
      practiceSessionNumber: state.practiceSessionNumber ?? 0,
      practiceSessionCount: state.practiceSessionCount ?? 1,
      qualifyingSessionNumber: state.qualifyingSessionNumber ?? 0,
      qualifyingSessionCount: state.qualifyingSessionCount ?? 1,
      minimumRequiredPitStops: state.minimumRequiredPitStops ?? 1,
      yellowZones: (state.yellowZones ?? []).map(zone => ({
        sectorIndex: zone.sectorIndex ?? null,
        isAutomatic: zone.isAutomatic,
        reason: zone.reason,
        participantName: zone.participantName ?? null
      })),
      participants: state.participants.map(participant => ({
        position: participant.position,
        displayName: participant.displayName,
        themeColor: participant.themeColor,
        teamName: participant.teamName ?? null,
        teamColor: participant.teamColor ?? null,
        status: participant.status,
        isConnected: participant.isConnected,
        completedLaps: participant.completedLaps,
        currentSector: participant.currentSector,
        trackProgress: participant.trackProgress,
        currentLapSeconds: participant.currentLapSeconds,
        lastLapSeconds: participant.lastLapSeconds ?? null,
        bestLapSeconds: participant.bestLapSeconds ?? null,
        gapToLeaderSeconds: participant.gapToLeaderSeconds ?? null,
        intervalSeconds: participant.intervalSeconds ?? null,
        isInPitLane: participant.isInPitLane,
        isInServiceZone: participant.isInServiceZone,
        pitServiceElapsedSeconds: participant.pitServiceElapsedSeconds,
        pitServiceRequirementMet: participant.pitServiceRequirementMet,
        completedPitServices: participant.completedPitServices,
        timePenaltySeconds: participant.timePenaltySeconds ?? 0,
        pendingTimePenaltySeconds: participant.pendingTimePenaltySeconds ?? 0,
        isServingTimePenalty: participant.isServingTimePenalty ?? false,
        hasPendingDriveThrough: participant.hasPendingDriveThrough ?? false,
        isServingDriveThrough: participant.isServingDriveThrough ?? false,
        driveThroughOverdue: participant.driveThroughOverdue ?? false,
        penalties: participant.penalties.map(publicPenalty)
      })),
      serverTime: state.serverTime
    },
    results: results.map(result => {
      const names = new Map(result.participants.map(item => [item.id, item.displayName]));
      return {
        phase: result.phase,
        label: result.label,
        sessionNumber: result.sessionNumber,
        sessionCount: result.sessionCount,
        isComplete: result.isComplete,
        completedAt: result.completedAt,
        sessionName: result.sessionName,
        trackName: result.trackName ?? null,
        fastestDriverName: result.fastestParticipantId
          ? names.get(result.fastestParticipantId) ?? null
          : null,
        fastestLapSeconds: result.fastestLapSeconds ?? null,
        participants: result.participants.map(participant => ({
          position: participant.position,
          displayName: participant.displayName,
          themeColor: participant.themeColor,
          teamName: participant.teamName ?? null,
          teamColor: participant.teamColor ?? null,
          status: participant.status,
          completedLaps: participant.completedLaps,
          bestLapSeconds: participant.bestLapSeconds ?? null,
          raceTotalSeconds: participant.raceTotalSeconds ?? null,
          adjustedRaceTotalSeconds: participant.adjustedRaceTotalSeconds ?? null,
          gapToLeaderSeconds: participant.gapToLeaderSeconds ?? null,
          timePenaltySeconds: participant.timePenaltySeconds,
          penalties: participant.penalties.map(publicPenalty)
        }))
      };
    })
  };
}

function publicPenalty(penalty: PenaltySnapshot) {
  return {
    kind: penalty.kind,
    valueSeconds: penalty.valueSeconds ?? null,
    gridPlaces: penalty.gridPlaces ?? null,
    reason: penalty.reason,
    issuedAt: penalty.issuedAt,
    isServed: penalty.isServed,
    isRevoked: penalty.isRevoked,
    isPostRaceAdjustment: penalty.isPostRaceAdjustment ?? false,
    isAutomatic: penalty.isAutomatic ?? false
  };
}

async function tokenDigest(token: string): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(token)));
  return base64Url(digest);
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
