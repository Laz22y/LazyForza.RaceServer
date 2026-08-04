import { describe, expect, it } from "vitest";
import { RaceCore } from "../src/race-core";
import type { LapCompleted, LoginRequest, TelemetryUpdate } from "../src/protocol";

describe("RaceCore", () => {
  it("supports exactly twelve participants and rejects the thirteenth", () => {
    const core = createCore();
    for (let index = 1; index <= 12; index++)
      expect(core.login(login(`车手${index}`)).ok).toBe(true);

    const rejected = core.login(login("车手13"));
    expect(rejected.ok).toBe(false);
    if (!rejected.ok) expect(rejected.code).toBe("roomFull");
  });

  it("supports a room configured for one participant", () => {
    const core = createCore(1);
    expect(core.login(login("单人练习")).ok).toBe(true);
    const rejected = core.login(login("第二名车手"));
    expect(rejected.ok).toBe(false);
    if (!rejected.ok) expect(rejected.code).toBe("roomFull");
  });

  it("does not let telemetry advance authoritative race laps", () => {
    const core = createCore();
    const first = connect(core, "甲");
    connect(core, "乙");
    expect(core.applySession({ phase: "race", totalRaceLaps: 5 }).ok).toBe(true);

    core.updateTelemetry(first, { ...telemetry(), completedLaps: 99, trackProgress: 0.9 });

    expect(core.snapshot().participants.find(participant => participant.id === first)?.completedLaps).toBe(0);
  });

  it("advances only unique valid lap events and never trusts client lap number", () => {
    const core = createCore();
    const first = connect(core, "甲");
    connect(core, "乙");
    core.applySession({ phase: "race", totalRaceLaps: 5 });
    const event = lap("lap-a", 80, true, 999);

    expect(core.completeLap(first, event).ok).toBe(true);
    expect(core.completeLap(first, event).ok).toBe(true);
    expect(core.completeLap(first, lap("lap-invalid", 79, false, 1)).ok).toBe(true);

    const participant = core.snapshot().participants.find(candidate => candidate.id === first)!;
    expect(participant.completedLaps).toBe(1);
    expect(participant.lastLapSeconds).toBe(80);
  });

  it("credits at most one pit service per telemetry increment", () => {
    const core = createCore();
    const first = connect(core, "甲");
    core.updateTelemetry(first, {
      ...telemetry(),
      isInPitLane: true,
      isInServiceZone: true,
      pitServiceElapsedSeconds: 999,
      pitServiceRequirementMet: true,
      completedPitServices: 1
    });
    let participant = core.snapshot().participants.find(candidate => candidate.id === first)!;
    expect(participant.pitServiceElapsedSeconds).toBe(60);
    expect(participant.completedPitServices).toBe(1);

    core.updateTelemetry(first, {
      ...telemetry(),
      isInPitLane: true,
      isInServiceZone: true,
      pitServiceElapsedSeconds: 5,
      pitServiceRequirementMet: true,
      completedPitServices: 1
    });
    participant = core.snapshot().participants.find(candidate => candidate.id === first)!;
    expect(participant.completedPitServices).toBe(1);
  });

  it("orders qualifying by best lap and resumes with the same identity", () => {
    const core = createCore();
    const firstLogin = core.login(login("甲"));
    if (!firstLogin.ok) throw new Error("login failed");
    const second = connect(core, "乙");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 });
    core.completeLap(firstLogin.participantId, lap("a", 81, true, 1));
    core.completeLap(second, lap("b", 80, true, 1));

    expect(core.snapshot().participants.map(participant => participant.displayName)).toEqual(["乙", "甲"]);
    core.disconnect(firstLogin.participantId);
    const resumed = core.login({ ...login("甲"), resumeToken: firstLogin.resumeToken });
    expect(resumed.ok).toBe(true);
    if (resumed.ok) expect(resumed.participantId).toBe(firstLogin.participantId);
  });

  it("allows a solo race and handles red/green flags", () => {
    const core = createCore();
    connect(core, "甲");
    expect(core.applySession({ phase: "race" }).ok).toBe(true);
    expect(core.applyFlag({ flag: "red", message: "赛道有事故" }).ok).toBe(true);
    expect(core.snapshot().phase).toBe("suspended");
    expect(core.applyFlag({ flag: "green" }).ok).toBe(true);
    expect(core.snapshot().phase).toBe("race");
  });

  it("uses the leader crossing for the automatic chequered flag", () => {
    const core = createCore();
    const leader = connect(core, "甲");
    const second = connect(core, "乙");
    core.applySession({ phase: "race", totalRaceLaps: 2 });
    expect(core.applyFlag({ flag: "chequered" }).ok).toBe(false);
    core.completeLap(second, lap("s1", 65, true, 1));
    core.completeLap(leader, lap("l1", 64, true, 1));
    core.completeLap(leader, lap("l2", 64, true, 2));
    expect(core.snapshot().flag).toBe("chequered");
    expect(core.snapshot().participants.find(item => item.id === second)?.status).not.toBe("finished");
    core.completeLap(second, lap("s2", 65, true, 2));
    expect(core.snapshot().phase).toBe("finished");
  });

  it("keeps a manual sector yellow after an automatic hazard recovers", () => {
    const core = createCore();
    const first = connect(core, "甲");
    connect(core, "乙");
    core.applyRoomSettings({ sessionName: "测试", totalRaceLaps: 5, sectorCount: 3,
      automaticYellowEnabled: true, slowSpeedKph: 12, slowDurationSeconds: 3,
      severeLateralOffsetMeters: 25, recoveryDurationSeconds: 3 });
    core.applySession({ phase: "race" });
    const start = new Date("2026-08-03T12:00:00Z");
    const slow = { ...telemetry(), speedKph: 5, currentSector: 1 };
    core.updateTelemetry(first, slow, start);
    core.updateTelemetry(first, slow, new Date(start.getTime() + 3_100));
    expect(core.snapshot().yellowZones.some(zone => zone.isAutomatic && zone.sectorIndex === 1)).toBe(true);
    core.applyFlag({ flag: "yellow", sectorIndex: 2, message: "人工管制" });
    const recovered = { ...slow, speedKph: 100 };
    core.updateTelemetry(first, recovered, new Date(start.getTime() + 4_000));
    core.updateTelemetry(first, recovered, new Date(start.getTime() + 7_100));
    expect(core.snapshot().yellowZones.some(zone => zone.isAutomatic)).toBe(false);
    expect(core.snapshot().yellowZones.some(zone => !zone.isAutomatic && zone.sectorIndex === 2)).toBe(true);
  });

  it("escalates automatic hazards in two sectors to a full-course yellow", () => {
    const core = createCore();
    const first = connect(core, "甲");
    const second = connect(core, "乙");
    core.applySession({ phase: "race" });
    const start = new Date("2026-08-03T12:00:00Z");
    const slow = { ...telemetry(), speedKph: 5 };
    core.updateTelemetry(first, { ...slow, currentSector: 0 }, start);
    core.updateTelemetry(second, { ...slow, currentSector: 2 }, start);
    core.updateTelemetry(first, { ...slow, currentSector: 0 }, new Date(start.getTime() + 3_100));
    core.updateTelemetry(second, { ...slow, currentSector: 2 }, new Date(start.getTime() + 3_100));

    const zones = core.snapshot().yellowZones;
    expect(zones.some(zone => zone.isAutomatic && zone.sectorIndex === null)).toBe(true);
    expect(zones.some(zone => zone.sectorIndex === 0)).toBe(true);
    expect(zones.some(zone => zone.sectorIndex === 2)).toBe(true);
  });

  it("disables teams and validates the configured track identity", () => {
    const core = createCore();
    const trackId = "123e4567-e89b-42d3-a456-426614174000", hash = "A".repeat(64);
    expect(core.applyRoomSettings({ sessionName: "测试", totalRaceLaps: 5, sectorCount: 3,
      automaticYellowEnabled: true, slowSpeedKph: 12, slowDurationSeconds: 3,
      severeLateralOffsetMeters: 25, recoveryDurationSeconds: 3,
      allowTeams: false, trackName: "测试环道", trackId, trackPackageHash: hash }).ok).toBe(true);
    expect(core.login(login("错误赛道")).ok).toBe(false);
    const joined = core.login({ ...login("甲"), trackId, trackPackageHash: hash, sectorCount: 3 });
    expect(joined.ok).toBe(true);
    expect(core.snapshot().participants[0].teamName).toBeNull();
    expect(core.snapshot().allowTeams).toBe(false);
  });

  it("publishes automatic blue flags and qualifying automatic yellows", () => {
    const core = createCore();
    const approaching = connect(core, "甲"), recipient = connect(core, "乙");
    core.applySession({ phase: "race", totalRaceLaps: 5 });
    core.completeLap(approaching, lap("lap-a", 60, true, 1));
    core.updateTelemetry(approaching, { ...telemetry(), trackProgress: .40 });
    core.updateTelemetry(recipient, { ...telemetry(), trackProgress: .48 });
    expect(core.snapshot().blueFlags).toEqual([expect.objectContaining({
      recipientParticipantId: recipient, approachingParticipantId: approaching
    })]);

    core.applySession({ phase: "lobby" });
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 });
    const start = new Date("2026-08-03T12:00:00Z"), slow = { ...telemetry(), speedKph: 5 };
    core.updateTelemetry(approaching, slow, start);
    core.updateTelemetry(approaching, slow, new Date(start.getTime() + 3_100));
    expect(core.snapshot().flag).toBe("yellow");
    expect(core.snapshot().banner?.kind).not.toBe("yellowFlag");
  });

  it("runs out lap, formation lap and the five-light start sequence", () => {
    const core = createCore();
    connect(core, "甲");
    expect(core.applySession({ phase: "outLap" }).ok).toBe(true);
    expect(core.applySession({ phase: "formationLap" }).ok).toBe(true);
    const now = new Date("2026-08-04T10:00:00Z");
    expect(core.applySession({ phase: "countdown", countdownSeconds: 0 }, now).ok).toBe(true);
    const start = core.snapshot(now);
    expect(start.startSequenceAt).toBe(now.toISOString());
    const duration = Date.parse(start.startsAt!) - Date.parse(start.startSequenceAt!);
    expect(duration).toBeGreaterThanOrEqual(5_000);
    expect(duration).toBeLessThanOrEqual(8_000);
    core.tick(now);
    expect(core.snapshot(now).illuminatedStartLights).toBe(1);
    core.tick(new Date(now.getTime() + 4_100));
    expect(core.snapshot().illuminatedStartLights).toBe(5);
    core.tick(new Date(Date.parse(start.startsAt!) + 1));
    expect(core.snapshot().phase).toBe("race");
    expect(core.snapshot().startLightsOut).toBe(true);
  });

  it("penalizes a false start once", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const now = new Date("2026-08-04T10:00:00Z");
    core.applySession({ phase: "countdown", countdownSeconds: 0 }, now);
    core.tick(now);
    core.updateTelemetry(participantId, { ...telemetry(), speedKph: 18 }, new Date(now.getTime() + 50));
    let penalties = core.snapshot().participants[0].penalties;
    expect(penalties).toHaveLength(1);
    expect(penalties[0]).toMatchObject({ kind: "time", valueSeconds: 5 });
    expect(penalties[0].reason).toContain("抢跑");
    core.updateTelemetry(participantId, { ...telemetry(), trackProgress: .6 }, new Date(now.getTime() + 500));
    penalties = core.snapshot().participants[0].penalties;
    expect(penalties).toHaveLength(1);
  });

  it("keeps qualifying open for a final flying lap already in progress", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const now = new Date("2026-08-04T10:00:00Z");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 1 }, now);
    core.updateTelemetry(participantId, { ...telemetry(), currentLapSeconds: 31 }, new Date(now.getTime() + 59_000));
    core.tick(new Date(now.getTime() + 60_001));
    expect(core.snapshot().phase).toBe("qualifying");
    expect(core.snapshot().qualifyingTimeExpired).toBe(true);
    expect(core.snapshot().participants[0].qualifyingFinalLapPending).toBe(true);
    expect(core.completeLap(participantId, lap("final-flying-lap", 71.25, true, 1)).ok).toBe(true);
    expect(core.snapshot().phase).toBe("grid");
    expect(core.snapshot().participants[0].bestLapSeconds).toBe(71.25);
  });
});

function createCore(maximumParticipants = 12): RaceCore {
  return new RaceCore({ sessionName: "测试赛事", maximumParticipants, totalRaceLaps: 5 });
}

function connect(core: RaceCore, name: string): string {
  const result = core.login(login(name));
  if (!result.ok) throw new Error(result.message);
  return result.participantId;
}

function login(displayName: string): LoginRequest {
  return {
    password: "not-checked-by-core",
    displayName,
    themeColor: "#20C8D8",
    teamName: "测试车队",
    clientVersion: "test",
    trackId: null,
    trackRevision: null,
    trackPackageHash: null
  };
}

function telemetry(): TelemetryUpdate {
  return {
    clientMonotonicMilliseconds: 1,
    trackProgress: 0.5,
    lateralOffsetMeters: 0,
    mapX: 10,
    mapY: 20,
    speedKph: 120,
    completedLaps: 0,
    currentSector: 1,
    currentLapSeconds: 30,
    isInPitLane: false,
    isInServiceZone: false,
    isTelemetryValid: true,
    isPausedOrRewinding: false,
    gripCondition: "slightlyReduced",
    pitServiceElapsedSeconds: 0,
    pitServiceRequirementMet: false,
    completedPitServices: 0
  };
}

function lap(eventId: string, lapSeconds: number, isValid: boolean, lapNumber: number): LapCompleted {
  return {
    eventId,
    lapNumber,
    lapSeconds,
    sectorSeconds: [lapSeconds / 2, lapSeconds / 2],
    isValid,
    invalidReason: isValid ? null : "test-invalid",
    clientMonotonicMilliseconds: 10_000
  };
}
