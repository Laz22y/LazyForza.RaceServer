import { describe, expect, it } from "vitest";
import type { SessionSnapshot } from "../src/protocol";
import {
  createPublicTimingAccess,
  normalizePublicTimingAccess,
  publicTimingPayload,
  verifyPublicTimingToken
} from "../src/public-timing";

describe("public live timing", () => {
  it("creates an independent rotatable token and stores only its digest", async () => {
    const generated = await createPublicTimingAccess(new Date("2026-08-29T08:30:00Z"));

    expect(generated.token).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(generated.stored.hash).not.toBe(generated.token);
    expect(await verifyPublicTimingToken(generated.token, generated.stored)).toBe(true);
    expect(await verifyPublicTimingToken("admin-password", generated.stored)).toBe(false);
    expect(normalizePublicTimingAccess({ hash: "invalid", generatedAt: "bad" })).toBeNull();

    const rotated = await createPublicTimingAccess(new Date("2026-08-29T08:31:00Z"));
    expect(await verifyPublicTimingToken(generated.token, rotated.stored)).toBe(false);
    expect(await verifyPublicTimingToken(rotated.token, rotated.stored)).toBe(true);
  });

  it("publishes timing and results without internal ids or map coordinates", () => {
    const participantId = "11111111-1111-1111-1111-111111111111";
    const penalty = {
      id: "22222222-2222-2222-2222-222222222222",
      participantId, kind: "time" as const, valueSeconds: 5, reason: "碰撞责任",
      issuedAt: "2026-08-29T08:29:00Z", isServed: false, isRevoked: false
    };
    const participant = {
      id: participantId, position: 1, displayName: "Driver One", themeColor: "#42D7E8",
      teamName: "Team One", status: "onTrack" as const, isConnected: true, isReady: true,
      completedLaps: 4, currentSector: 2, trackProgress: 0.62, mapX: 123.5, mapY: 456.75,
      speedKph: 218, currentLapSeconds: 42.125, lastLapSeconds: 61.25,
      bestLapSeconds: 59.123, gapToLeaderSeconds: 0, intervalSeconds: null,
      isInPitLane: false, isInServiceZone: false, pitServiceElapsedSeconds: 0,
      pitServiceRequirementMet: false, completedPitServices: 1, gripCondition: "unknown" as const,
      bestSectorSeconds: [20.1, 19.8, 19.223], penalties: [penalty],
      lastSeenAt: "2026-08-29T08:30:00Z", qualifyingFinalLapPending: false,
      pendingTimePenaltySeconds: 5
    };
    const state = {
      revision: 7, sessionName: "直播测试", phase: "race", flag: "yellow",
      flagMessage: "赛道事故", trackId: "track-id", trackRevision: "revision-id",
      trackPackageHash: "package-hash", totalRaceLaps: 10,
      startsAt: "2026-08-29T08:20:00Z", illuminatedStartLights: 0, startLightsOut: true,
      qualifyingTimeExpired: false, fastestParticipantId: participantId,
      fastestLapSeconds: 59.123, fastestSectorSeconds: [20.1, 19.8, 19.223],
      participants: [participant], serverTime: "2026-08-29T08:30:00Z",
      yellowZones: [{ sectorIndex: 2, isAutomatic: true, reason: "事故车辆", participantId,
        participantName: "Driver One" }], sectorCount: 3, allowTeams: true,
      blueFlags: [], driversPerTeam: 6, teams: [], chequeredImminent: false,
      investigations: [{ id: "private-investigation" }], observers: [{ id: "private-observer" }],
      minimumRequiredPitStops: 2
    } as unknown as SessionSnapshot;
    const results = [{
      id: "33333333-3333-3333-3333-333333333333", phase: "qualifying" as const,
      label: "Q1", sessionNumber: 1, sessionCount: 1, isComplete: true,
      completedAt: "2026-08-29T08:25:00Z", sessionName: "直播测试",
      fastestParticipantId: participantId, fastestLapSeconds: 59.123,
      participants: [{ id: participantId, position: 1, displayName: "Driver One",
        themeColor: "#42D7E8", status: "finished" as const, completedLaps: 3,
        trackProgress: 1, bestLapSeconds: 59.123, raceTotalSeconds: 180.5,
        adjustedRaceTotalSeconds: 185.5, gapToLeaderSeconds: 0,
        timePenaltySeconds: 5, penalties: [penalty] }]
    }];

    const payload = publicTimingPayload(state, results);
    const json = JSON.stringify(payload);

    expect(payload.state.fastestDriverName).toBe("Driver One");
    expect(payload.results).toHaveLength(1);
    expect(json).toContain('"pendingTimePenaltySeconds":5');
    expect(json).toContain('"participantName":"Driver One"');
    expect(json).not.toContain("participantId");
    expect(json).not.toContain("mapX");
    expect(json).not.toContain("mapY");
    expect(json).not.toContain("lastSeenAt");
    expect(json).not.toContain("investigations");
    expect(json).not.toContain("observers");
    expect(json).not.toContain(participantId);
  });
});
