import { describe, expect, it } from "vitest";
import { defaultQualifyingEliminations, RaceCore } from "../src/race-core";
import type { LapCompleted, LoginRequest, SessionCommand, TelemetryUpdate } from "../src/protocol";

describe("RaceCore", () => {
  it.each(["practice", "qualifying", "race"] as const)(
    "opens collision investigations during %s",
    phase => {
      const core = createCore();
      const reporter = connect(core, "甲"), other = connect(core, "乙");
      expect(core.setAutomaticCollisionInvestigations(true).ok).toBe(true);
      const started = new Date("2026-08-21T10:00:00Z");
      const command: SessionCommand = phase === "qualifying"
        ? { phase, qualifyingMinutes: 10 }
        : phase === "race" ? { phase, totalRaceLaps: 5 } : { phase };
      expect(core.applySession(command, started).ok).toBe(true);
      const motion = { ...telemetry(), hasWorldPosition: true, worldY: 0, worldZ: 50,
        hasWorldVelocity: true, worldVelocityY: 0, worldVelocityZ: 0 };
      core.updateTelemetry(reporter,
        { ...motion, worldX: 100, worldVelocityX: 20 }, new Date(started.getTime() + 100));
      core.updateTelemetry(other,
        { ...motion, worldX: 105, worldVelocityX: 10 }, new Date(started.getTime() + 100));
      core.updateTelemetry(other,
        { ...motion, worldX: 101.5, worldVelocityX: 10 }, new Date(started.getTime() + 400));
      core.updateTelemetry(reporter, {
        ...motion, worldX: 100, worldVelocityX: 20, impactSequence: 1,
        impactMagnitudeMps: 4.4, impactSpeedLossMps: 2.2,
        impactWorldX: 100, impactWorldY: 0, impactWorldZ: 50,
        impactAgeMilliseconds: 80, impactWorldVelocityX: 20,
        impactWorldVelocityY: 0, impactWorldVelocityZ: 0
      }, new Date(started.getTime() + 480));

      expect(core.snapshot().investigations).toHaveLength(1);
    });

  it("requires fresh nearby evidence and keeps collision details for post-race review", () => {
    const core = createCore();
    const reporter = connect(core, "甲");
    const other = connect(core, "乙");
    expect(core.applyRoomSettings({
      ...core.roomSettings(), totalRaceLaps: 1, minimumRequiredPitStops: 0,
      automaticCollisionInvestigationsEnabled: true
    }).ok).toBe(true);
    const started = new Date("2026-08-13T12:00:00Z");
    const peer = { ...telemetry(), hasWorldPosition: true, worldX: 101.5, worldY: 0, worldZ: 50,
      velocityX: 10, velocityY: 0, velocityZ: 0, hasWorldVelocity: true,
      worldVelocityX: 10, worldVelocityY: 0, worldVelocityZ: 0 };
    const impact = { ...telemetry(), hasWorldPosition: true, worldX: 100, worldY: 0, worldZ: 50,
      velocityX: 20, velocityY: 0, velocityZ: 0, impactSequence: 1, impactMagnitudeMps: 4.4,
      impactSpeedLossMps: 2.2, impactWorldX: 100, impactWorldY: 0, impactWorldZ: 50,
      impactAgeMilliseconds: 80, hasWorldVelocity: true,
      worldVelocityX: 20, worldVelocityY: 0, worldVelocityZ: 0,
      impactWorldVelocityX: 20, impactWorldVelocityY: 0, impactWorldVelocityZ: 0 };
    core.updateTelemetry(other, peer, started);
    core.updateTelemetry(reporter, impact, new Date(started.getTime() + 80));
    expect(core.snapshot().investigations).toHaveLength(0);

    core.applySession({ phase: "race", totalRaceLaps: 1 }, new Date(started.getTime() + 1_000));
    core.updateTelemetry(reporter, { ...impact, impactSequence: 0, impactMagnitudeMps: 0,
      impactSpeedLossMps: 0, impactAgeMilliseconds: 0 }, new Date(started.getTime() + 1_700));
    core.updateTelemetry(other, { ...peer, worldX: 105 }, new Date(started.getTime() + 1_700));
    core.updateTelemetry(other, peer, new Date(started.getTime() + 2_000));
    core.updateTelemetry(reporter, { ...impact, impactSequence: 2 }, new Date(started.getTime() + 2_080));
    const investigation = core.snapshot().investigations?.[0];
    expect(investigation?.relatedParticipantIds).toEqual([reporter, other]);
    expect(investigation?.collisionEvidence).toMatchObject({
      horizontalDistanceMeters: 1.5,
      bothDriversReportedImpact: false
    });
    expect(investigation?.collisionEvidence?.approachDistanceReductionMeters).toBeGreaterThanOrEqual(3);
    expect(core.setAutomaticCollisionInvestigations(false).ok).toBe(true);
    expect(core.roomSettings().automaticCollisionInvestigationsEnabled).toBe(false);
    expect(core.snapshot().investigations).toHaveLength(1);

    core.completeLap(reporter, lap("collision-finish", 60, true, 1), new Date(started.getTime() + 60_000));
    core.disconnect(other, new Date(started.getTime() + 61_000));
    expect(core.snapshot().phase).toBe("finished");
    expect(core.resolveInvestigation({ investigationId: investigation!.id, applyPenalty: true,
      participantId: other, kind: "time", valueSeconds: 4, reason: "赛后复核确认责任" }).ok).toBe(true);
    expect(core.snapshot().penalties).toContainEqual(expect.objectContaining({
      participantId: other, valueSeconds: 4, isPostRaceAdjustment: true
    }));
  });

  it("does not treat official smashable-object evidence as a vehicle collision", () => {
    const core = createCore();
    const reporter = connect(core, "甲"), other = connect(core, "乙");
    expect(core.applyRoomSettings({
      ...core.roomSettings(), automaticCollisionInvestigationsEnabled: true
    }).ok).toBe(true);
    const started = new Date("2026-08-20T12:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);
    const motion = { ...telemetry(), hasWorldPosition: true, worldY: 0, worldZ: 50,
      hasWorldVelocity: true, worldVelocityY: 0, worldVelocityZ: 0 };
    core.updateTelemetry(reporter, { ...motion, worldX: 100, worldVelocityX: 20 },
      new Date(started.getTime() + 100));
    core.updateTelemetry(other, { ...motion, worldX: 105, worldVelocityX: 10 },
      new Date(started.getTime() + 100));
    core.updateTelemetry(other, { ...motion, worldX: 101.5, worldVelocityX: 10 },
      new Date(started.getTime() + 400));
    core.updateTelemetry(reporter, {
      ...motion, worldX: 100, worldVelocityX: 20, impactSequence: 1,
      impactMagnitudeMps: 5, impactSpeedLossMps: 2, impactWorldX: 100,
      impactWorldY: 0, impactWorldZ: 50, impactAgeMilliseconds: 80,
      impactWorldVelocityX: 20, impactWorldVelocityY: 0, impactWorldVelocityZ: 0,
      impactSmashableVelDiff: 5, impactSmashableMass: 25
    }, new Date(started.getTime() + 480));

    expect(core.snapshot().investigations).toHaveLength(0);
  });

  it("groups repeated contacts for the same driver pair into one investigation", () => {
    const core = createCore();
    const reporter = connect(core, "甲"), other = connect(core, "乙");
    core.applyRoomSettings({
      ...core.roomSettings(), automaticCollisionInvestigationsEnabled: true
    });
    const started = new Date("2026-08-20T11:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 10 }, started);
    const motion = { ...telemetry(), hasWorldPosition: true, worldY: 0, worldZ: 50,
      hasWorldVelocity: true, worldVelocityY: 0, worldVelocityZ: 0 };
    core.updateTelemetry(other, {
      ...motion, worldX: 101, worldVelocityX: 10, impactSequence: 1,
      impactMagnitudeMps: 3.5, impactWorldX: 101, impactWorldY: 0, impactWorldZ: 50,
      impactWorldVelocityX: 10, impactWorldVelocityY: 0, impactWorldVelocityZ: 0
    }, new Date(started.getTime() + 1_000));
    core.updateTelemetry(reporter, {
      ...motion, worldX: 100, worldVelocityX: 20, impactSequence: 1,
      impactMagnitudeMps: 4.5, impactWorldX: 100, impactWorldY: 0, impactWorldZ: 50,
      impactAgeMilliseconds: 50, impactWorldVelocityX: 20,
      impactWorldVelocityY: 0, impactWorldVelocityZ: 0
    }, new Date(started.getTime() + 1_050));
    const first = core.snapshot().investigations?.[0];
    const firstBannerId = core.snapshot().banner?.id;

    core.updateTelemetry(other, {
      ...motion, worldX: 100.8, worldVelocityX: 9, impactSequence: 2,
      impactMagnitudeMps: 4, impactWorldX: 100.8, impactWorldY: 0, impactWorldZ: 50,
      impactWorldVelocityX: 9, impactWorldVelocityY: 0, impactWorldVelocityZ: 0
    }, new Date(started.getTime() + 3_000));
    core.updateTelemetry(reporter, {
      ...motion, worldX: 100, worldVelocityX: 21, impactSequence: 2,
      impactMagnitudeMps: 6, impactWorldX: 100, impactWorldY: 0, impactWorldZ: 50,
      impactAgeMilliseconds: 50, impactWorldVelocityX: 21,
      impactWorldVelocityY: 0, impactWorldVelocityZ: 0
    }, new Date(started.getTime() + 3_050));

    const grouped = core.snapshot().investigations;
    expect(grouped).toHaveLength(1);
    expect(grouped?.[0]).toMatchObject({
      id: first?.id,
      collisionEvidence: {
        contactCount: 2,
        impactMagnitudeMps: 6
      }
    });
    expect(grouped?.[0].collisionEvidence?.horizontalDistanceMeters).toBeCloseTo(.8, 3);
    expect(grouped?.[0].offense).toContain("连续疑似车辆接触（2 次");
    expect(core.snapshot().banner?.id).toBe(firstBannerId);
    expect(core.events().filter(event => event.type === "collisionInvestigationOpened")).toHaveLength(1);

    core.updateTelemetry(other, {
      ...motion, worldX: 101, worldVelocityX: 10, impactSequence: 3,
      impactMagnitudeMps: 4, impactWorldX: 101, impactWorldY: 0, impactWorldZ: 50,
      impactWorldVelocityX: 10, impactWorldVelocityY: 0, impactWorldVelocityZ: 0
    }, new Date(started.getTime() + 16_000));
    core.updateTelemetry(reporter, {
      ...motion, worldX: 100, worldVelocityX: 20, impactSequence: 3,
      impactMagnitudeMps: 5, impactWorldX: 100, impactWorldY: 0, impactWorldZ: 50,
      impactAgeMilliseconds: 50, impactWorldVelocityX: 20,
      impactWorldVelocityY: 0, impactWorldVelocityZ: 0
    }, new Date(started.getTime() + 16_050));
    expect(core.snapshot().investigations).toHaveLength(2);
    expect(core.events().filter(event => event.type === "collisionInvestigationOpened")).toHaveLength(2);
  });

  it("does not treat parallel braking without an approach trajectory as contact", () => {
    const core = createCore();
    const reporter = connect(core, "甲"), other = connect(core, "乙");
    core.applyRoomSettings({
      ...core.roomSettings(), automaticCollisionInvestigationsEnabled: true
    });
    const started = new Date("2026-08-20T12:10:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);
    const motion = { ...telemetry(), hasWorldPosition: true, worldY: 0, worldZ: 50,
      hasWorldVelocity: true, worldVelocityX: 20, worldVelocityY: 0, worldVelocityZ: 0 };
    core.updateTelemetry(reporter, { ...motion, worldX: 100 }, new Date(started.getTime() + 100));
    core.updateTelemetry(other, { ...motion, worldX: 102 }, new Date(started.getTime() + 100));
    core.updateTelemetry(other, { ...motion, worldX: 102 }, new Date(started.getTime() + 400));
    core.updateTelemetry(reporter, {
      ...motion, worldX: 100, impactSequence: 1, impactMagnitudeMps: 4,
      impactSpeedLossMps: 2, impactWorldX: 100, impactWorldY: 0, impactWorldZ: 50,
      impactAgeMilliseconds: 80, impactWorldVelocityX: 20,
      impactWorldVelocityY: 0, impactWorldVelocityZ: 0
    }, new Date(started.getTime() + 480));

    expect(core.snapshot().investigations).toHaveLength(0);
  });

  it("matches delayed multiplayer contact from both drivers by their impact anchors", () => {
    const core = createCore();
    const reporter = connect(core, "甲"), other = connect(core, "乙");
    core.applyRoomSettings({
      ...core.roomSettings(), automaticCollisionInvestigationsEnabled: true
    });
    const started = new Date("2026-08-21T15:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);
    const motion = { ...telemetry(), hasWorldPosition: true, worldY: 0, worldZ: 50,
      hasWorldVelocity: true, worldVelocityY: 0, worldVelocityZ: 0 };
    core.updateTelemetry(reporter, { ...motion, worldX: 100, worldVelocityX: 20 },
      new Date(started.getTime() + 100));
    core.updateTelemetry(other, {
      ...motion, worldX: 110, worldVelocityX: 10, impactSequence: 1,
      impactMagnitudeMps: 4.8, impactWorldX: 105.4, impactWorldY: 0, impactWorldZ: 50,
      impactWorldVelocityX: 10, impactWorldVelocityY: 0, impactWorldVelocityZ: 0,
      impactAgeMilliseconds: 50
    }, new Date(started.getTime() + 1_000));
    core.updateTelemetry(other, {
      ...motion, worldX: 115, worldVelocityX: 10, impactSequence: 1, impactMagnitudeMps: 4.8
    }, new Date(started.getTime() + 1_500));
    core.updateTelemetry(reporter, {
      ...motion, worldX: 100, worldVelocityX: 20, impactSequence: 1,
      impactMagnitudeMps: 4.5, impactWorldX: 100, impactWorldY: 0, impactWorldZ: 50,
      impactWorldVelocityX: 20, impactWorldVelocityY: 0, impactWorldVelocityZ: 0,
      impactAgeMilliseconds: 50
    }, new Date(started.getTime() + 1_600));

    const evidence = core.snapshot().investigations?.[0].collisionEvidence;
    expect(evidence?.bothDriversReportedImpact).toBe(true);
    expect(evidence?.horizontalDistanceMeters).toBeCloseTo(5.4, 3);
  });

  it("rejects paired braking reports without relative motion", () => {
    const core = createCore();
    const reporter = connect(core, "甲"), other = connect(core, "乙");
    core.applyRoomSettings({
      ...core.roomSettings(), automaticCollisionInvestigationsEnabled: true
    });
    const started = new Date("2026-08-21T15:10:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);
    const motion = { ...telemetry(), hasWorldPosition: true, worldY: 0, worldZ: 50,
      hasWorldVelocity: true, worldVelocityX: 20, worldVelocityY: 0, worldVelocityZ: 0 };
    core.updateTelemetry(other, {
      ...motion, worldX: 102, impactSequence: 1, impactMagnitudeMps: 4,
      impactSpeedLossMps: 2, impactWorldX: 102, impactWorldY: 0, impactWorldZ: 50,
      impactWorldVelocityX: 20, impactWorldVelocityY: 0, impactWorldVelocityZ: 0
    }, new Date(started.getTime() + 1_000));
    core.updateTelemetry(reporter, {
      ...motion, worldX: 100, impactSequence: 1, impactMagnitudeMps: 4,
      impactSpeedLossMps: 2, impactWorldX: 100, impactWorldY: 0, impactWorldZ: 50,
      impactWorldVelocityX: 20, impactWorldVelocityY: 0, impactWorldVelocityZ: 0,
      impactAgeMilliseconds: 50
    }, new Date(started.getTime() + 1_050));

    expect(core.snapshot().investigations).toHaveLength(0);
  });

  it("supports exactly twelve participants and rejects the thirteenth", () => {
    const core = createCore();
    for (let index = 1; index <= 12; index++) {
      const team = index <= 6 ? { teamId: "team-1", teamName: "车队 1" } : { teamId: "team-2", teamName: "车队 2" };
      expect(core.login({ ...login(`车手${index}`), ...team }).ok).toBe(true);
    }

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

  it("allows an observer during a race without taking a driver slot", () => {
    const core = createCore();
    for (let index = 1; index <= 12; index++) {
      const team = index <= 6 ? { teamId: "team-1", teamName: "车队 1" } : { teamId: "team-2", teamName: "车队 2" };
      expect(core.login({ ...login(`车手${index}`), ...team }).ok).toBe(true);
    }
    expect(core.applySession({ phase: "race", totalRaceLaps: 5 }).ok).toBe(true);

    const observer = core.login({
      ...login("转播席 A"),
      teamName: null,
      teamId: null,
      isObserver: true
    });
    expect(observer).toMatchObject({ ok: true, isObserver: true });
    if (!observer.ok) throw new Error(observer.message);
    expect(core.snapshot().participants).toHaveLength(12);
    expect(core.snapshot().observers).toEqual([
      expect.objectContaining({ id: observer.participantId, displayName: "转播席 A" })
    ]);
    expect(core.setReady(observer.participantId, { isReady: true }).ok).toBe(false);

    const resumed = core.login({
      ...login("转播席 A"),
      teamName: null,
      teamId: null,
      isObserver: true,
      resumeToken: observer.resumeToken
    });
    expect(resumed).toMatchObject({ ok: true, resumed: true, isObserver: true });
    expect(core.snapshot().observers).toHaveLength(1);

    expect(core.disconnect(observer.participantId)).toBe(true);
    expect(core.snapshot().observers).toEqual([]);
    expect(core.events().some(event => event.type === "observerDisconnected")).toBe(true);
  });

  it("returns only event rows newer than the control panel cursor", () => {
    const core = createCore();
    connect(core, "甲");
    const firstSequence = Math.max(...core.events().map(event => event.sequence));

    connect(core, "乙");
    const incremental = core.events(250, firstSequence);

    expect(incremental.length).toBeGreaterThan(0);
    expect(incremental.every(event => event.sequence > firstSequence)).toBe(true);
    expect(Math.max(...incremental.map(event => event.sequence)))
      .toBe(Math.max(...core.events().map(event => event.sequence)));
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
    expect(participant.pitServiceElapsedSeconds).toBe(999);
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

    core.updateTelemetry(first, {
      ...telemetry(),
      mapX: 9_999,
      mapY: 9_999,
      isInPitLane: true,
      isInServiceZone: true,
      isTelemetryValid: false,
      isPausedOrRewinding: true,
      pitServiceElapsedSeconds: 6,
      pitServiceRequirementMet: true,
      completedPitServices: 2
    });
    participant = core.snapshot().participants.find(candidate => candidate.id === first)!;
    expect(participant.mapX).toBe(10);
    expect(participant.mapY).toBe(20);
    expect(participant.pitServiceElapsedSeconds).toBe(6);
    expect(participant.completedPitServices).toBe(2);
  });

  it("accepts each integer manual time penalty from one to six seconds", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    for (let seconds = 1; seconds <= 6; seconds++)
      expect(core.applyPenalty({ participantId, kind: "time", valueSeconds: seconds,
        gridPlaces: null, reason: `手动罚时 ${seconds} 秒` }).ok).toBe(true);
    expect(core.snapshot().participants[0].penalties.map(item => item.valueSeconds))
      .toEqual([1, 2, 3, 4, 5, 6]);
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

  it("runs Q1 Q2 Q3 with default eliminations and publishes the complete grid", () => {
    const core = createCore(6);
    const drivers = Array.from({ length: 6 }, (_, index) => connect(core, `车手${index + 1}`));
    const started = new Date("2026-08-12T10:00:00Z");
    const qualifyingCommand: SessionCommand = {
      phase: "qualifying",
      qualifyingSessionCount: 3,
      qualifyingSessionMinutes: [1, 2, 3],
      qualifyingEliminationCounts: [null, null]
    };
    expect(core.applySession(qualifyingCommand, started).ok).toBe(true);
    let state = core.snapshot(started);
    expect(state.qualifyingEliminationCounts).toEqual([2, 1]);
    drivers.forEach((driver, index) => core.completeLap(
      driver, lap(`q1-${index}`, 66 - index, true, 1), new Date(started.getTime() + 20_000 + index)));
    core.tick(new Date(Date.parse(state.qualifyingEndsAt!) + 1));

    state = core.snapshot();
    expect(state.qualifyingSessionNumber).toBe(1);
    expect(state.qualifyingTimeExpired).toBe(true);
    expect(state.qualifyingEndsAt).toBeNull();
    expect(state.participants.filter(item => item.qualifyingEligible).length).toBe(4);
    expect(state.participants.filter(item => item.qualifyingEliminatedInSession === 1).length).toBe(2);
    expect(core.applySession(qualifyingCommand, new Date(started.getTime() + 65_000)).ok).toBe(true);
    state = core.snapshot();
    expect(state.qualifyingSessionNumber).toBe(2);
    const q2Drivers = drivers.slice(2);
    q2Drivers.forEach((driver, index) => core.completeLap(driver, lap(`q2-${index}`, 70 - index, true, 1)));
    core.tick(new Date(Date.parse(core.snapshot().qualifyingEndsAt!) + 1));

    state = core.snapshot();
    expect(state.qualifyingSessionNumber).toBe(2);
    expect(state.qualifyingTimeExpired).toBe(true);
    expect(state.qualifyingEndsAt).toBeNull();
    expect(state.participants.filter(item => item.qualifyingEligible).length).toBe(3);
    expect(core.applySession(qualifyingCommand, new Date(started.getTime() + 190_000)).ok).toBe(true);
    state = core.snapshot();
    expect(state.qualifyingSessionNumber).toBe(3);
    drivers.slice(3).forEach((driver, index) => core.completeLap(driver, lap(`q3-${index}`, 64 - index, true, 1)));
    core.tick(new Date(Date.parse(core.snapshot().qualifyingEndsAt!) + 1));

    state = core.snapshot();
    expect(state.phase).toBe("grid");
    expect(state.participants.map(item => item.id)).toEqual([
      drivers[5], drivers[4], drivers[3], drivers[2], drivers[1], drivers[0]
    ]);
    expect(state.participants[0].qualifyingSessionBestLapSeconds).toEqual([61, 67, 62]);
  });

  it("keeps the default elimination table aligned for every 2-12 driver field", () => {
    const expected = [[0, 0], [1, 0], [1, 1], [1, 1], [2, 1], [2, 1],
      [2, 2], [2, 2], [3, 2], [3, 2], [3, 3]];
    for (let drivers = 2; drivers <= 12; drivers++)
      expect(defaultQualifyingEliminations(drivers, 3)).toEqual(expected[drivers - 2]);
  });

  it("keeps legacy single-session qualifying unchanged", () => {
    const core = createCore();
    connect(core, "甲");
    const started = new Date("2026-08-12T11:00:00Z");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 }, started);
    const state = core.snapshot(started);
    expect(state.qualifyingSessionCount).toBe(1);
    expect(state.qualifyingSessionNumber).toBe(1);
    expect(state.qualifyingEliminationCounts).toEqual([]);
    expect(Date.parse(state.qualifyingEndsAt!) - started.getTime()).toBe(10 * 60_000);
  });

  it("defaults every configured practice session to sixty minutes", () => {
    const core = createCore();
    connect(core, "甲");
    const started = new Date("2026-08-12T12:00:00Z");
    expect(core.applySession({ phase: "practice" }, started).ok).toBe(true);
    const state = core.snapshot(started);
    expect(state.practiceSessionNumber).toBe(1);
    expect(state.practiceSessionCount).toBe(1);
    expect(state.practiceSessionMinutes).toEqual([60]);
    expect(Date.parse(state.practiceEndsAt!) - started.getTime()).toBe(60 * 60_000);
    expect(core.applySession({ phase: "practice", practiceSessionCount: 3 }, started).ok).toBe(true);
    expect(core.snapshot(started).practiceSessionMinutes).toEqual([60, 60, 60]);
  });

  it("runs FP1 FP2 FP3 with custom durations and no eliminations", () => {
    const core = createCore();
    const first = connect(core, "甲");
    const second = connect(core, "乙");
    const started = new Date("2026-08-12T13:00:00Z");
    const practiceCommand: SessionCommand = {
      phase: "practice",
      practiceSessionCount: 3,
      practiceSessionMinutes: [1, 2, 3]
    };
    expect(core.applySession(practiceCommand, started).ok).toBe(true);

    let state = core.snapshot(started);
    core.completeLap(first, lap("fp1-a", 70, true, 1));
    core.completeLap(second, lap("fp1-b", 71, true, 1));
    core.tick(new Date(Date.parse(state.practiceEndsAt!) + 1));

    state = core.snapshot();
    expect(state.practiceSessionNumber).toBe(1);
    expect(state.practiceTimeExpired).toBe(true);
    expect(state.practiceEndsAt).toBeNull();
    expect(state.participants.find(item => item.id === first)?.practiceSessionBestLapSeconds?.[0]).toBe(70);
    expect(core.applySession(practiceCommand, new Date(started.getTime() + 65_000)).ok).toBe(true);
    state = core.snapshot();
    expect(state.practiceSessionNumber).toBe(2);
    expect(state.participants).toHaveLength(2);
    expect(state.participants.every(item => item.bestLapSeconds == null)).toBe(true);
    core.completeLap(first, lap("fp2-a", 69, true, 1));
    core.completeLap(second, lap("fp2-b", 68, true, 1));
    core.tick(new Date(Date.parse(core.snapshot().practiceEndsAt!) + 1));

    state = core.snapshot();
    expect(state.practiceSessionNumber).toBe(2);
    expect(state.practiceTimeExpired).toBe(true);
    expect(state.practiceEndsAt).toBeNull();
    expect(core.applySession(practiceCommand, new Date(started.getTime() + 190_000)).ok).toBe(true);
    state = core.snapshot();
    expect(state.practiceSessionNumber).toBe(3);
    core.completeLap(first, lap("fp3-a", 67, true, 1));
    core.tick(new Date(Date.parse(state.practiceEndsAt!) + 1));

    state = core.snapshot();
    expect(state.phase).toBe("practice");
    expect(state.practiceTimeExpired).toBe(true);
    expect(state.practiceEndsAt).toBeNull();
    expect(state.participants.every(item => item.status === "ready")).toBe(true);
    expect(state.participants.find(item => item.id === first)?.practiceSessionBestLapSeconds)
      .toEqual([70, 69, 67]);
  });

  it("keeps practice qualifying and race results after returning to the lobby", () => {
    const core = createCore();
    const driver = connect(core, "甲");
    const started = new Date("2026-08-12T14:00:00Z");

    core.applySession({ phase: "practice", practiceSessionMinutes: [1] }, started);
    core.completeLap(driver, lap("archive-fp", 70, true, 1), new Date(started.getTime() + 30_000));
    core.tick(new Date(Date.parse(core.snapshot().practiceEndsAt!) + 1));
    expect(core.results()).toMatchObject([{
      phase: "practice",
      label: "练习赛",
      isComplete: true,
      participants: [{ bestLapSeconds: 70 }]
    }]);
    core.applySession({ phase: "lobby" }, new Date(started.getTime() + 120_000));
    expect(core.results()).toHaveLength(1);

    core.applySession({ phase: "qualifying", qualifyingMinutes: 1 }, new Date(started.getTime() + 180_000));
    core.completeLap(driver, lap("archive-q", 68, true, 1), new Date(started.getTime() + 210_000));
    core.tick(new Date(Date.parse(core.snapshot().qualifyingEndsAt!) + 1));
    expect(core.snapshot().phase).toBe("grid");
    expect(core.results()[0].phase).toBe("qualifying");
    core.applySession({ phase: "lobby" }, new Date(started.getTime() + 300_000));

    core.applySession({ phase: "race", totalRaceLaps: 1 }, new Date(started.getTime() + 360_000));
    core.completeLap(driver, lap("archive-race", 75, true, 1), new Date(started.getTime() + 435_000));
    expect(core.snapshot().phase).toBe("finished");
    core.applySession({ phase: "lobby" }, new Date(started.getTime() + 480_000));

    expect(core.results().map(result => result.phase)).toEqual(["race", "qualifying", "practice"]);
    expect(core.results()[0].participants[0].adjustedRaceTotalSeconds).toBe(75);
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

  it("finishes the race when a trailing driver disconnects after the chequered flag", () => {
    const core = createCore();
    const leaderLogin = core.login(login("甲"));
    const trailingLogin = core.login(login("乙"));
    if (!leaderLogin.ok || !trailingLogin.ok) throw new Error("login failed");
    core.applySession({ phase: "race", totalRaceLaps: 2 });
    core.completeLap(leaderLogin.participantId, lap("l1", 64, true, 1));
    core.completeLap(trailingLogin.participantId, lap("s1", 65, true, 1));
    core.completeLap(leaderLogin.participantId, lap("l2", 64, true, 2));
    expect(core.snapshot().flag).toBe("chequered");

    expect(core.disconnect(trailingLogin.participantId)).toBe(true);
    expect(core.snapshot().phase).toBe("finished");
    const resumed = core.login({ ...login("乙"), resumeToken: trailingLogin.resumeToken });
    expect(resumed.ok).toBe(true);
    expect(core.snapshot().participants.find(item => item.id === trailingLogin.participantId)?.status)
      .toBe("didNotFinish");
    expect(core.snapshot().phase).toBe("finished");
  });

  it("keeps the race open and deduplicates a recovered lap when recovery is enabled", () => {
    const core = createCore();
    expect(core.applyRoomSettings({
      ...core.roomSettings(),
      disconnectedLapRecoveryEnabled: true
    }).ok).toBe(true);
    const leaderLogin = core.login(login("甲"));
    const trailingLogin = core.login(login("乙"));
    if (!leaderLogin.ok || !trailingLogin.ok) throw new Error("login failed");
    const started = new Date("2026-08-21T12:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 2 }, started);
    core.completeLap(leaderLogin.participantId, lap("recovery-l1", 64, true, 1),
      new Date(started.getTime() + 60_000));
    core.completeLap(trailingLogin.participantId, lap("recovery-s1", 65, true, 1),
      new Date(started.getTime() + 61_000));
    core.completeLap(leaderLogin.participantId, lap("recovery-l2", 64, true, 2),
      new Date(started.getTime() + 120_000));

    const disconnectedAt = new Date(started.getTime() + 121_000);
    expect(core.disconnect(trailingLogin.participantId, disconnectedAt)).toBe(true);
    expect(core.snapshot(disconnectedAt).phase).toBe("race");
    const resumed = core.login(
      { ...login("乙"), resumeToken: trailingLogin.resumeToken },
      new Date(started.getTime() + 122_000));
    expect(resumed.ok).toBe(true);
    expect(core.snapshot().participants.find(item => item.id === trailingLogin.participantId)?.status)
      .toBe("onTrack");

    const recovered = {
      ...lap("recovery-s2", 65, true, 2),
      isRecoveredAfterDisconnect: true
    };
    expect(core.completeLap(trailingLogin.participantId, recovered,
      new Date(started.getTime() + 123_000)).ok).toBe(true);
    expect(core.snapshot().phase).toBe("finished");
    expect(core.completeLap(trailingLogin.participantId, recovered,
      new Date(started.getTime() + 124_000)).ok).toBe(true);
    expect(core.snapshot().participants.find(item => item.id === trailingLogin.participantId)?.completedLaps)
      .toBe(2);
    expect(core.snapshot().disconnectedLapRecoveryEnabled).toBe(true);
  });

  it("finishes the race after an enabled recovery window expires", () => {
    const core = createCore();
    core.applyRoomSettings({ ...core.roomSettings(), disconnectedLapRecoveryEnabled: true });
    const leader = connect(core, "甲");
    const trailing = connect(core, "乙");
    const started = new Date("2026-08-21T13:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 2 }, started);
    core.completeLap(leader, lap("expiry-l1", 64, true, 1), new Date(started.getTime() + 60_000));
    core.completeLap(trailing, lap("expiry-s1", 65, true, 1), new Date(started.getTime() + 61_000));
    core.completeLap(leader, lap("expiry-l2", 64, true, 2), new Date(started.getTime() + 120_000));
    const disconnectedAt = new Date(started.getTime() + 121_000);
    core.disconnect(trailing, disconnectedAt);
    expect(core.snapshot(disconnectedAt).phase).toBe("race");

    expect(core.tick(new Date(disconnectedAt.getTime() + 31_000))).toBe(true);
    expect(core.snapshot().phase).toBe("finished");
    expect(core.snapshot().participants.find(item => item.id === trailing)?.status).toBe("didNotFinish");
  });

  it("releases a race-control disconnected display name and participant slot", () => {
    const core = createCore(1);
    const original = core.login(login("同名车手"));
    if (!original.ok) throw new Error(original.message);
    expect(core.disconnectAndReleaseClient(original.participantId).ok).toBe(true);
    expect(core.snapshot().participants).toHaveLength(0);
    const kickedRetry = core.login({ ...login("同名车手"), resumeToken: original.resumeToken });
    expect(kickedRetry).toMatchObject({ ok: false, code: "disconnectedByControl" });
    const replacement = core.login(login("同名车手"));
    expect(replacement.ok).toBe(true);
    if (replacement.ok) expect(replacement.participantId).not.toBe(original.participantId);
    expect(core.events().some(event => event.type === "participantRemoved" &&
      event.participantId === original.participantId)).toBe(true);
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

  it("treats the recorded pit branch as a legal route for yellow and track limits", () => {
    const core = createCore();
    const participantId = connect(core, "维修区车手");
    const started = new Date("2026-08-10T10:00:00Z");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 }, started);
    const pitBranch = {
      ...telemetry(),
      lateralOffsetMeters: 80,
      speedKph: 4,
      trackLengthMeters: 2_000,
      isOnPitRoute: true
    };

    core.updateTelemetry(participantId, pitBranch, started);
    core.updateTelemetry(participantId, {
      ...pitBranch,
      clientMonotonicMilliseconds: 4_001
    }, new Date(started.getTime() + 4_000));

    const snapshot = core.snapshot(new Date(started.getTime() + 4_000));
    expect(snapshot.flag).toBe("green");
    expect(snapshot.participants[0].trackLimitWarnings).toBe(0);
    expect(snapshot.participants[0].penalties).toHaveLength(0);
  });

  it("treats the recorded approach before the pit entry line as a legal route", () => {
    const core = createCore();
    const participantId = connect(core, "维修区入口车手");
    const started = new Date("2026-08-10T10:00:00Z");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 }, started);
    const pitApproach = {
      ...telemetry(),
      lateralOffsetMeters: 80,
      speedKph: 4,
      trackLengthMeters: 2_000,
      isApproachingPit: true,
      isOnPitRoute: false
    };

    core.updateTelemetry(participantId, pitApproach, started);
    core.updateTelemetry(participantId, {
      ...pitApproach,
      clientMonotonicMilliseconds: 4_001
    }, new Date(started.getTime() + 4_000));

    const snapshot = core.snapshot(new Date(started.getTime() + 4_000));
    expect(snapshot.flag).toBe("green");
    expect(snapshot.participants[0].trackLimitWarnings).toBe(0);
    expect(snapshot.participants[0].penalties).toHaveLength(0);
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

  it("keeps practice open for a final lap before the controller starts FP2", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const now = new Date("2026-08-12T14:00:00Z");
    core.applySession({
      phase: "practice",
      practiceSessionCount: 2,
      practiceSessionMinutes: [1, 1]
    }, now);
    core.updateTelemetry(participantId, { ...telemetry(), currentLapSeconds: 31 },
      new Date(now.getTime() + 59_000));
    core.tick(new Date(now.getTime() + 60_001));
    expect(core.snapshot().practiceTimeExpired).toBe(true);
    expect(core.snapshot().participants[0].practiceFinalLapPending).toBe(true);

    expect(core.completeLap(participantId, lap("practice-final-lap", 71.25, true, 1)).ok).toBe(true);
    expect(core.snapshot().practiceSessionNumber).toBe(1);
    expect(core.snapshot().practiceTimeExpired).toBe(true);
    expect(core.snapshot().practiceEndsAt).toBeNull();
    expect(core.snapshot().participants[0].practiceSessionBestLapSeconds?.[0]).toBe(71.25);
    expect(core.applySession({ phase: "practice", practiceSessionCount: 2,
      practiceSessionMinutes: [1, 1] }, new Date(now.getTime() + 120_000)).ok).toBe(true);
    expect(core.snapshot().practiceSessionNumber).toBe(2);
    expect(core.snapshot().practiceTimeExpired).toBe(false);
  });

  it("publishes race total time and ranks finished drivers by adjusted time", () => {
    const core = createCore();
    const first = connect(core, "甲"), second = connect(core, "乙");
    const started = new Date("2026-08-09T10:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 2 }, started);
    core.completeLap(first, lap("first-1", 60, true, 1), new Date(started.getTime() + 60_000));
    core.completeLap(second, lap("second-1", 61, true, 1), new Date(started.getTime() + 61_000));
    expect(core.snapshot(new Date(started.getTime() + 61_000)).participants[1].gapToLeaderSeconds).toBe(1);

    core.completeLap(first, lap("first-2", 60, true, 2), new Date(started.getTime() + 120_000));
    core.completeLap(second, lap("second-2", 60, true, 2), new Date(started.getTime() + 121_000));
    core.applyPenalty({ participantId: first, kind: "time", valueSeconds: 5, reason: "赛后加罚" });
    const result = core.snapshot(new Date(started.getTime() + 130_000));
    expect(result.phase).toBe("finished");
    expect(result.participants[0].id).toBe(second);
    expect(result.participants[0].adjustedRaceTotalSeconds).toBe(121);
    expect(result.participants[1].adjustedRaceTotalSeconds).toBe(125);
    expect(result.participants[0].gapToLeaderSeconds).toBe(0);
    expect(result.participants[1].gapToLeaderSeconds).toBe(4);
    expect(result.raceElapsedSeconds).toBe(121);
  });

  it("updates race deltas at common track progress without waiting for a lap event", () => {
    const core = createCore();
    const leader = connect(core, "甲"), trailing = connect(core, "乙");
    const started = new Date("2026-08-09T10:30:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);

    core.updateTelemetry(leader, { ...telemetry(), trackProgress: .10 },
      new Date(started.getTime() + 10_000));
    core.updateTelemetry(trailing, { ...telemetry(), trackProgress: .10 },
      new Date(started.getTime() + 11_000));
    expect(core.snapshot(new Date(started.getTime() + 11_000)).participants[1].gapToLeaderSeconds).toBe(1);

    core.updateTelemetry(leader, { ...telemetry(), trackProgress: .30 },
      new Date(started.getTime() + 20_000));
    core.updateTelemetry(trailing, { ...telemetry(), trackProgress: .30 },
      new Date(started.getTime() + 23_000));
    const refreshed = core.snapshot(new Date(started.getTime() + 23_000)).participants[1];
    expect(refreshed.gapToLeaderSeconds).toBe(3);
    expect(refreshed.intervalSeconds).toBe(3);
    expect(refreshed.completedLaps).toBe(0);
  });

  it("keeps first-lap Delta stable through small samples, overtakes and pit transit", () => {
    const core = createCore();
    const first = connect(core, "甲"), second = connect(core, "乙");
    const started = new Date("2026-08-09T10:40:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);

    core.updateTelemetry(first, { ...telemetry(), trackProgress: .98 },
      new Date(started.getTime() + 1_000));
    core.updateTelemetry(second, { ...telemetry(), trackProgress: .97 },
      new Date(started.getTime() + 2_000));
    core.updateTelemetry(first, { ...telemetry(), trackProgress: .01 },
      new Date(started.getTime() + 3_000));
    core.updateTelemetry(second, { ...telemetry(), trackProgress: .01 },
      new Date(started.getTime() + 4_000));
    expect(core.snapshot(new Date(started.getTime() + 4_000)).participants
      .find(item => item.id === second)?.gapToLeaderSeconds).toBeCloseTo(1, 3);

    for (let index = 1; index <= 100; index++) {
      const progress = .01 + index * .0015;
      const at = started.getTime() + (4 + index * .1) * 1_000;
      core.updateTelemetry(first, { ...telemetry(), trackProgress: progress }, new Date(at));
      core.updateTelemetry(second, { ...telemetry(), trackProgress: progress }, new Date(at + 1_000));
    }
    expect(core.snapshot(new Date(started.getTime() + 15_000)).participants
      .find(item => item.id === second)?.gapToLeaderSeconds).toBeCloseTo(1, 2);

    core.updateTelemetry(second, { ...telemetry(), trackProgress: .35 },
      new Date(started.getTime() + 20_000));
    core.updateTelemetry(first, { ...telemetry(), trackProgress: .34 },
      new Date(started.getTime() + 20_500));
    const afterPass = core.snapshot(new Date(started.getTime() + 20_500)).participants
      .find(item => item.id === first)?.gapToLeaderSeconds;
    expect(afterPass).toBeGreaterThan(0);
    expect(afterPass).toBeLessThan(5);

    core.updateTelemetry(second, {
      ...telemetry(), trackProgress: .85, isInPitLane: true, isOnPitRoute: true
    }, new Date(started.getTime() + 22_000));
    core.completeLap(second, lap("pit-lap", 60, true, 1),
      new Date(started.getTime() + 25_000));
    core.updateTelemetry(second, { ...telemetry(), completedLaps: 1, trackProgress: .10 },
      new Date(started.getTime() + 30_000));
    const afterPit = core.snapshot(new Date(started.getTime() + 30_000)).participants
      .find(item => item.id === first)?.gapToLeaderSeconds;
    expect(afterPit).toBeGreaterThanOrEqual(0);
    expect(afterPit).toBeLessThan(10);
  });

  it("publishes direct pairwise Delta instead of subtracting different leader anchors", () => {
    const core = createCore();
    const leader = connect(core, "甲"), local = connect(core, "乙"), trailing = connect(core, "丙");
    const started = new Date("2026-08-09T10:42:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);

    core.updateTelemetry(leader, { ...telemetry(), trackProgress: .10 }, new Date(started.getTime() + 10_000));
    core.updateTelemetry(local, { ...telemetry(), trackProgress: .10 }, new Date(started.getTime() + 12_000));
    core.updateTelemetry(trailing, { ...telemetry(), trackProgress: .10 }, new Date(started.getTime() + 13_000));
    core.updateTelemetry(leader, { ...telemetry(), trackProgress: .20 }, new Date(started.getTime() + 20_000));
    core.updateTelemetry(local, { ...telemetry(), trackProgress: .20 }, new Date(started.getTime() + 22_000));
    core.updateTelemetry(trailing, { ...telemetry(), trackProgress: .20 }, new Date(started.getTime() + 25_000));
    core.updateTelemetry(leader, { ...telemetry(), trackProgress: .30 }, new Date(started.getTime() + 30_000));
    core.updateTelemetry(local, { ...telemetry(), trackProgress: .30 }, new Date(started.getTime() + 38_000));

    const snapshot = core.snapshot(new Date(started.getTime() + 38_000));
    const localSnapshot = snapshot.participants.find(item => item.id === local)!;
    const trailingSnapshot = snapshot.participants.find(item => item.id === trailing)!;
    expect(localSnapshot.gapToLeaderSeconds).toBe(8);
    expect(trailingSnapshot.gapToLeaderSeconds).toBe(5);
    expect(trailingSnapshot.raceDeltaSecondsByReference?.[local]).toBe(3);
    expect(localSnapshot.raceDeltaSecondsByReference?.[trailing]).toBe(-3);
  });

  it("keeps direct pairwise Delta stable across twelve laps", () => {
    const core = createCore();
    const leader = connect(core, "甲"), trailing = connect(core, "乙");
    const started = new Date("2026-08-09T10:44:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 20 }, started);

    for (let completedLap = 1; completedLap <= 12; completedLap++) {
      const lapStart = (completedLap - 1) * 60_000;
      core.updateTelemetry(leader, {
        ...telemetry(), completedLaps: completedLap - 1, trackProgress: .25
      }, new Date(started.getTime() + lapStart + 15_000));
      core.updateTelemetry(trailing, {
        ...telemetry(), completedLaps: completedLap - 1, trackProgress: .25
      }, new Date(started.getTime() + lapStart + 17_000));
      core.updateTelemetry(leader, {
        ...telemetry(), completedLaps: completedLap - 1, trackProgress: .75
      }, new Date(started.getTime() + lapStart + 45_000));
      core.updateTelemetry(trailing, {
        ...telemetry(), completedLaps: completedLap - 1, trackProgress: .75
      }, new Date(started.getTime() + lapStart + 47_000));
      core.completeLap(leader, lap(`leader-${completedLap}`, 60, true, completedLap),
        new Date(started.getTime() + lapStart + 60_000));
      core.completeLap(trailing, lap(`trailing-${completedLap}`, 60, true, completedLap),
        new Date(started.getTime() + lapStart + 62_000));

      const trailingSnapshot = core.snapshot(new Date(started.getTime() + lapStart + 62_000))
        .participants.find(item => item.id === trailing)!;
      expect(trailingSnapshot.raceDeltaSecondsByReference?.[leader]).toBeCloseTo(2, 3);
    }
  });

  it("waits for fresh telemetry before a reconnected driver affects live order", () => {
    const core = createCore();
    const weakLogin = core.login(login("弱网"));
    if (!weakLogin.ok) throw new Error(weakLogin.message);
    const weak = weakLogin.participantId;
    const healthyLeader = connect(core, "正常甲"), healthyTrailing = connect(core, "正常乙");
    const started = new Date("2026-08-09T10:46:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);

    core.updateTelemetry(weak, { ...telemetry(), trackProgress: .90 }, new Date(started.getTime() + 10_000));
    core.updateTelemetry(healthyLeader, { ...telemetry(), trackProgress: .20 }, new Date(started.getTime() + 12_000));
    core.updateTelemetry(healthyTrailing, { ...telemetry(), trackProgress: .20 }, new Date(started.getTime() + 15_000));
    core.updateTelemetry(healthyLeader, { ...telemetry(), trackProgress: .50 }, new Date(started.getTime() + 22_000));
    core.updateTelemetry(healthyLeader, { ...telemetry(), trackProgress: .60 }, new Date(started.getTime() + 23_000));
    core.updateTelemetry(healthyTrailing, { ...telemetry(), trackProgress: .50 }, new Date(started.getTime() + 25_000));

    core.disconnect(weak, new Date(started.getTime() + 30_000));
    const resumed = core.login(
      { ...login("弱网"), resumeToken: weakLogin.resumeToken },
      new Date(started.getTime() + 31_000));
    expect(resumed.ok).toBe(true);

    const snapshot = core.snapshot(new Date(started.getTime() + 31_000));
    expect(snapshot.participants.map(item => item.id)).toEqual([
      healthyLeader, healthyTrailing, weak
    ]);
    expect(snapshot.participants[1].raceDeltaSecondsByReference?.[healthyLeader]).toBeCloseTo(3, 3);
    expect(snapshot.participants[2].raceDeltaSecondsByReference?.[healthyLeader]).toBeUndefined();
  });

  it("keeps a seconds gap when the leader crosses before the trailing driver", () => {
    const core = createCore();
    const leader = connect(core, "甲"), trailing = connect(core, "乙");
    const started = new Date("2026-08-09T10:45:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 5 }, started);

    core.updateTelemetry(leader, { ...telemetry(), trackProgress: .90 },
      new Date(started.getTime() + 50_000));
    core.updateTelemetry(trailing, { ...telemetry(), trackProgress: .90 },
      new Date(started.getTime() + 55_000));
    core.completeLap(leader, lap("leader-crossing", 60, true, 1),
      new Date(started.getTime() + 60_000));

    const trailingSnapshot = core.snapshot(new Date(started.getTime() + 60_000))
      .participants.find(item => item.id === trailing);
    expect(trailingSnapshot?.completedLaps).toBe(0);
    expect(trailingSnapshot?.gapToLeaderSeconds).toBe(5);
  });

  it("freezes race elapsed time during a red flag", () => {
    const core = createCore();
    connect(core, "甲");
    const started = new Date("2026-08-09T11:00:00Z");
    core.applySession({ phase: "race" }, started);
    core.applyFlag({ flag: "red", message: "事故处理" }, new Date(started.getTime() + 10_000));
    const redFlag = core.snapshot(new Date(started.getTime() + 30_000));
    expect(redFlag.suspendedFromPhase).toBe("race");
    expect(redFlag.raceElapsedSeconds).toBe(10);
    core.applyFlag({ flag: "green" }, new Date(started.getTime() + 40_000));
    expect(core.snapshot(new Date(started.getTime() + 50_000)).raceElapsedSeconds).toBe(20);
  });

  it("does not publish a race clock when qualifying is suspended", () => {
    const core = createCore();
    connect(core, "甲");
    const started = new Date("2026-08-09T11:30:00Z");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 }, started);
    core.applyFlag({ flag: "red", message: "排位赛红旗" }, new Date(started.getTime() + 10_000));
    const snapshot = core.snapshot(new Date(started.getTime() + 30_000));
    expect(snapshot.suspendedFromPhase).toBe("qualifying");
    expect(snapshot.raceElapsedSeconds).toBeNull();
    expect(snapshot.participants[0].raceTotalSeconds).toBeNull();
  });

  it("warns for three minor cuts, then adds time, while a severe cut is penalized immediately", () => {
    const core = createCore();
    expect(core.applyRoomSettings({ ...core.roomSettings(), trackLimitMode: "automatic" }).ok).toBe(true);
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-09T12:00:00Z");
    core.applySession({ phase: "race" }, started);
    for (let incident = 0; incident < 4; incident++) {
      const incidentAt = new Date(started.getTime() + (incident * 2 + 1) * 1_000);
      const startProgress = .10 + incident * .05;
      const monotonic = 10_000 + incident * 2_000;
      const outside = {
        ...telemetry(), clientMonotonicMilliseconds: monotonic, trackProgress: startProgress,
        lateralOffsetMeters: 20, trackToleranceMeters: 18, speedKph: 36, trackLengthMeters: 1_000
      };
      core.updateTelemetry(participantId, outside, incidentAt);
      core.updateTelemetry(participantId, {
        ...outside, clientMonotonicMilliseconds: monotonic + 300, trackProgress: startProgress + .015
      }, new Date(incidentAt.getTime() + 300));
      core.updateTelemetry(participantId, {
        ...outside, clientMonotonicMilliseconds: monotonic + 500,
        trackProgress: startProgress + .020, lateralOffsetMeters: 0
      }, new Date(incidentAt.getTime() + 500));
      core.updateTelemetry(participantId, {
        ...outside, clientMonotonicMilliseconds: monotonic + 950,
        trackProgress: startProgress + .025, lateralOffsetMeters: 0
      }, new Date(incidentAt.getTime() + 950));
    }
    let participant = core.snapshot().participants[0];
    expect(participant.trackLimitWarnings).toBe(0);
    expect(participant.penalties.filter(item => item.kind === "warning")).toHaveLength(3);
    expect(participant.penalties.filter(item => item.kind === "time")).toHaveLength(1);
    expect(participant.timePenaltySeconds).toBe(5);

    const severe = {
      ...telemetry(), clientMonotonicMilliseconds: 20_000, trackProgress: .60,
      lateralOffsetMeters: 30, trackToleranceMeters: 18, speedKph: 36, trackLengthMeters: 1_000
    };
    core.updateTelemetry(participantId, severe, new Date(started.getTime() + 10_000));
    core.updateTelemetry(participantId,
      { ...severe, clientMonotonicMilliseconds: 20_300, trackProgress: .65 },
      new Date(started.getTime() + 10_300));
    core.updateTelemetry(participantId,
      { ...severe, clientMonotonicMilliseconds: 20_500, trackProgress: .67, lateralOffsetMeters: 0 },
      new Date(started.getTime() + 10_500));
    core.updateTelemetry(participantId,
      { ...severe, clientMonotonicMilliseconds: 20_950, trackProgress: .68, lateralOffsetMeters: 0 },
      new Date(started.getTime() + 10_950));
    participant = core.snapshot().participants[0];
    expect(participant.penalties.filter(item => item.kind === "time")).toHaveLength(2);
    expect(participant.timePenaltySeconds).toBe(10);
    expect(participant.penalties.at(-1)?.reason).toContain("严重切弯");
  });

  it("does not penalize an unprofitable deviation or pit approach and excludes a cut lap from fastest", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-09T12:20:00Z");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 }, started);
    const outside = {
      ...telemetry(), clientMonotonicMilliseconds: 10_000, trackProgress: .20,
      lateralOffsetMeters: 24, trackToleranceMeters: 18, speedKph: 36, trackLengthMeters: 1_000
    };
    core.updateTelemetry(participantId, outside, new Date(started.getTime() + 1_000));
    core.updateTelemetry(participantId,
      { ...outside, clientMonotonicMilliseconds: 11_000, trackProgress: .21 },
      new Date(started.getTime() + 2_000));
    core.updateTelemetry(participantId,
      { ...outside, clientMonotonicMilliseconds: 11_200, trackProgress: .211, lateralOffsetMeters: 0 },
      new Date(started.getTime() + 2_200));
    core.updateTelemetry(participantId,
      { ...outside, clientMonotonicMilliseconds: 11_700, trackProgress: .212, lateralOffsetMeters: 0 },
      new Date(started.getTime() + 2_700));
    expect(core.snapshot().participants[0].penalties).toHaveLength(0);

    core.updateTelemetry(participantId, {
      ...outside, clientMonotonicMilliseconds: 12_000, trackProgress: .30,
      lateralOffsetMeters: 40, isApproachingPit: true
    }, new Date(started.getTime() + 3_000));
    core.updateTelemetry(participantId, {
      ...outside, clientMonotonicMilliseconds: 12_500, trackProgress: .50,
      lateralOffsetMeters: 40, isApproachingPit: true
    }, new Date(started.getTime() + 3_500));
    expect(core.snapshot().participants[0].penalties).toHaveLength(0);

    expect(core.completeLap(participantId, {
      ...lap("cut-lap", 50, true, 1), isBestLapEligible: false
    }, new Date(started.getTime() + 50_000)).ok).toBe(true);
    const snapshot = core.snapshot(new Date(started.getTime() + 50_000));
    expect(snapshot.participants[0].completedLaps).toBe(1);
    expect(snapshot.participants[0].bestLapSeconds).toBeNull();
    expect(snapshot.fastestLapSeconds).toBeNull();
  });

  it("serves a pending time penalty before tire service and records race events", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-09T12:25:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 2 }, started);
    expect(core.applyPenalty({ participantId, kind: "time", valueSeconds: 2, reason: "测试罚时" }, started).ok).toBe(true);
    const stopped = {
      ...telemetry(), isInPitLane: true, isInServiceZone: true, speedKph: 0,
      pitServiceElapsedSeconds: 1.5, pitServiceRequirementMet: false
    };
    core.updateTelemetry(participantId, stopped, new Date(started.getTime() + 10_000));
    core.updateTelemetry(participantId, stopped, new Date(started.getTime() + 11_000));
    let participant = core.snapshot(new Date(started.getTime() + 11_000)).participants[0];
    expect(participant.isServingTimePenalty).toBe(true);
    expect(participant.pitServiceElapsedSeconds).toBe(0);
    core.updateTelemetry(participantId, stopped, new Date(started.getTime() + 12_100));
    participant = core.snapshot(new Date(started.getTime() + 12_100)).participants[0];
    expect(participant.isServingTimePenalty).toBe(false);
    expect(participant.pendingTimePenaltySeconds).toBe(0);
    expect(participant.penaltyServiceCompleted).toBe(true);
    expect(core.events().some(event => event.type === "penaltyServiceCompleted")).toBe(true);
  });

  it("counts two finish-line crossings for a drive-through then applies a fixed 20s adjustment", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-09T13:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 10 }, started);
    expect(core.applyPenalty({ participantId, kind: "driveThrough", reason: "测试通过维修区" }, started).ok).toBe(true);
    expect(core.snapshot(started).participants[0].driveThroughLapsRemaining).toBe(2);

    core.completeLap(participantId, lap("dt-lap-1", 60, true, 1), new Date(started.getTime() + 60_000));
    expect(core.snapshot().participants[0].driveThroughLapsRemaining).toBe(1);
    core.completeLap(participantId, lap("dt-lap-2", 61, true, 2), new Date(started.getTime() + 121_000));
    expect(core.snapshot().participants[0].driveThroughLapsRemaining).toBe(0);
    core.completeLap(participantId, lap("dt-lap-3", 62, true, 3), new Date(started.getTime() + 183_000));

    const overdue = core.snapshot(new Date(started.getTime() + 183_000)).participants[0];
    expect(overdue.hasPendingDriveThrough).toBe(false);
    expect(overdue.driveThroughOverdue).toBe(true);
    expect(overdue.pendingTimePenaltySeconds).toBe(0);
    expect(overdue.timePenaltySeconds).toBe(20);
    expect(overdue.penalties.some(item => item.isPostRaceAdjustment && !item.isServed)).toBe(true);
    expect(core.events().some(event => event.type === "driveThroughOverdue")).toBe(true);
  });

  it("serves a drive-through only by continuous pit transit and rejects a stopped visit", () => {
    const started = new Date("2026-08-09T13:10:00Z");
    const core = createCore();
    const participantId = connect(core, "甲");
    core.applySession({ phase: "race", totalRaceLaps: 10 }, started);
    core.applyPenalty({ participantId, kind: "driveThrough", reason: "连续通过" }, started);
    const inPit = { ...telemetry(), isInPitLane: true, speedKph: 55 };
    core.updateTelemetry(participantId, inPit, new Date(started.getTime() + 10_000));
    expect(core.snapshot().participants[0].isServingDriveThrough).toBe(true);
    core.updateTelemetry(participantId, { ...inPit, trackProgress: .55, speedKph: 48 }, new Date(started.getTime() + 12_000));
    core.updateTelemetry(participantId, { ...inPit, isInPitLane: false, trackProgress: .60, speedKph: 80 }, new Date(started.getTime() + 14_000));
    expect(core.snapshot().participants[0].hasPendingDriveThrough).toBe(false);

    const stoppedCore = createCore();
    const stoppedId = connect(stoppedCore, "乙");
    stoppedCore.applySession({ phase: "race", totalRaceLaps: 10 }, started);
    stoppedCore.applyPenalty({ participantId: stoppedId, kind: "driveThrough", reason: "停车失败" }, started);
    const stopped = { ...telemetry(), isInPitLane: true, speedKph: 0 };
    stoppedCore.updateTelemetry(stoppedId, stopped, new Date(started.getTime() + 5_000));
    stoppedCore.updateTelemetry(stoppedId, stopped, new Date(started.getTime() + 6_100));
    stoppedCore.updateTelemetry(stoppedId, { ...stopped, isInPitLane: false, speedKph: 60 }, new Date(started.getTime() + 8_000));
    expect(stoppedCore.snapshot().participants[0].hasPendingDriveThrough).toBe(true);
    expect(stoppedCore.events().some(event => event.type === "driveThroughAttemptFailed")).toBe(true);
  });

  it("converts a drive-through issued in the final three laps and preserves exact fastest-lap sectors", () => {
    const started = new Date("2026-08-09T13:20:00Z");
    const late = createCore();
    const lateId = connect(late, "甲");
    late.applySession({ phase: "race", totalRaceLaps: 3 }, started);
    late.applyPenalty({ participantId: lateId, kind: "driveThrough", reason: "最后三圈处罚" }, started);
    expect(late.snapshot().participants[0]).toMatchObject({
      hasPendingDriveThrough: false,
      pendingTimePenaltySeconds: 0,
      timePenaltySeconds: 20
    });
    expect(late.snapshot().participants[0].penalties[0].isPostRaceAdjustment).toBe(true);

    const qualifying = createCore();
    const first = connect(qualifying, "甲");
    const second = connect(qualifying, "乙");
    qualifying.applySession({ phase: "qualifying", qualifyingMinutes: 10 }, started);
    qualifying.completeLap(first, { ...lap("fastest-a", 60, true, 1), sectorSeconds: [20, 20, 20] });
    qualifying.completeLap(second, { ...lap("fastest-b", 59, true, 1), sectorSeconds: [18, 21, 20] });
    expect(qualifying.snapshot().fastestLapSectorSeconds).toEqual([18, 21, 20]);
    expect(qualifying.snapshot().fastestSectorSeconds).toEqual([18, 20, 20]);
  });

  it("settles pending time after the flag and never serves or adds automatic pit penalties post-race", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-12T10:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 1 }, started);
    expect(core.applyPenalty({ participantId, kind: "time", valueSeconds: 5, reason: "赛中罚时" }, started).ok).toBe(true);
    core.completeLap(participantId, lap("finish-with-time", 60, true, 1), new Date(started.getTime() + 60_000));
    let snapshot = core.snapshot(new Date(started.getTime() + 60_000));
    expect(snapshot.participants[0]).toMatchObject({
      status: "finished", pendingTimePenaltySeconds: 0, timePenaltySeconds: 5,
      adjustedRaceTotalSeconds: 65
    });
    expect(snapshot.participants[0].penalties[0].isPostRaceAdjustment).toBe(true);

    const postRacePit = {
      ...telemetry(), isInPitLane: true, isInServiceZone: true,
      isPausedOrRewinding: true, speedKph: 120, pitSpeedLimitKph: 80
    };
    core.updateTelemetry(participantId, postRacePit, new Date(started.getTime() + 70_000));
    core.updateTelemetry(participantId, { ...postRacePit, isPausedOrRewinding: false },
      new Date(started.getTime() + 71_000));
    snapshot = core.snapshot(new Date(started.getTime() + 71_000));
    expect(snapshot.penalties).toHaveLength(1);
    expect(snapshot.participants[0].hasPendingDriveThrough).toBe(false);
    expect(snapshot.participants[0].isServingTimePenalty).toBe(false);
    expect(snapshot.penalties?.some(item => item.reason.includes("维修区超速"))).toBe(false);

    expect(core.applyPenalty({ participantId, kind: "driveThrough", reason: "赛后人工判罚" }).ok).toBe(true);
    snapshot = core.snapshot();
    expect(snapshot.participants[0].timePenaltySeconds).toBe(25);
    expect(snapshot.participants[0].hasPendingDriveThrough).toBe(false);
    expect(snapshot.penalties?.at(-1)?.isPostRaceAdjustment).toBe(true);
  });

  it("opens an investigation in review-only mode and lets race control resolve, edit and cancel it", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-12T11:00:00Z");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 }, started);
    const outside = {
      ...telemetry(), clientMonotonicMilliseconds: 20_000, trackProgress: .60,
      lateralOffsetMeters: 20, speedKph: 36, trackLengthMeters: 1_000
    };
    core.updateTelemetry(participantId, outside, new Date(started.getTime() + 1_000));
    core.updateTelemetry(participantId,
      { ...outside, clientMonotonicMilliseconds: 20_300, trackProgress: .62 },
      new Date(started.getTime() + 1_300));
    core.updateTelemetry(participantId,
      { ...outside, clientMonotonicMilliseconds: 20_500, trackProgress: .625, lateralOffsetMeters: 0 },
      new Date(started.getTime() + 1_500));
    core.updateTelemetry(participantId,
      { ...outside, clientMonotonicMilliseconds: 20_950, trackProgress: .63, lateralOffsetMeters: 0 },
      new Date(started.getTime() + 1_950));
    let snapshot = core.snapshot(new Date(started.getTime() + 2_000));
    expect(snapshot.participants[0].penalties).toHaveLength(0);
    expect(snapshot.investigations).toHaveLength(1);
    expect(snapshot.investigations?.[0]).toMatchObject({ status: "pending", lapNumber: 1 });
    expect(snapshot.banner).toMatchObject({ kind: "information", isInvestigation: true });

    const investigation = snapshot.investigations![0];
    expect(core.resolveInvestigation({
      investigationId: investigation.id, applyPenalty: true, kind: "time", valueSeconds: 4,
      reason: "总控确认获利"
    }).ok).toBe(true);
    snapshot = core.snapshot();
    expect(snapshot.investigations?.[0].status).toBe("penalized");
    const penalty = snapshot.penalties![0];
    expect(penalty).toMatchObject({ valueSeconds: 4, investigationId: investigation.id });

    expect(core.updatePenalty({ penaltyId: penalty.id, valueSeconds: 2, reason: "复核后改为 2 秒", isRevoked: false }).ok).toBe(true);
    expect(core.snapshot().penalties?.[0].valueSeconds).toBe(2);
    expect(core.updatePenalty({ penaltyId: penalty.id, isRevoked: true }).ok).toBe(true);
    snapshot = core.snapshot();
    expect(snapshot.penalties?.[0].isRevoked).toBe(true);
    expect(snapshot.participants[0].penalties).toHaveLength(0);
  });

  it("detects a large shortcut and penalizes pit speeding once per visit", () => {
    const core = createCore();
    expect(core.applyRoomSettings({ ...core.roomSettings(), trackLimitMode: "automatic" }).ok).toBe(true);
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-09T12:30:00Z");
    core.applySession({ phase: "race" }, started);
    core.updateTelemetry(participantId, {
      ...telemetry(), clientMonotonicMilliseconds: 10_000, trackProgress: .10, trackLengthMeters: 1_000
    }, new Date(started.getTime() + 1_000));
    core.updateTelemetry(participantId, {
      ...telemetry(), clientMonotonicMilliseconds: 10_100, trackProgress: .52, trackLengthMeters: 1_000
    }, new Date(started.getTime() + 1_100));
    expect(core.snapshot().participants[0].penalties.filter(item => item.reason.includes("跨越约"))).toHaveLength(1);

    const speeding = {
      ...telemetry(), clientMonotonicMilliseconds: 11_000, trackProgress: .60,
      trackLengthMeters: 1_000, isInPitLane: true, speedKph: 92, pitSpeedLimitKph: 80
    };
    core.updateTelemetry(participantId, speeding, new Date(started.getTime() + 2_000));
    core.updateTelemetry(participantId, { ...speeding, clientMonotonicMilliseconds: 11_500 },
      new Date(started.getTime() + 2_500));
    core.updateTelemetry(participantId, { ...speeding, clientMonotonicMilliseconds: 12_000 },
      new Date(started.getTime() + 3_000));
    expect(core.snapshot().participants[0].penalties.filter(item => item.reason.includes("维修区超速"))).toHaveLength(1);
  });

  it("uses client route evidence to detect a corner cut without lateral excursion", () => {
    const core = createCore();
    expect(core.applyRoomSettings({ ...core.roomSettings(), trackLimitMode: "automatic" }).ok).toBe(true);
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-09T12:35:00Z");
    core.applySession({ phase: "race" }, started);
    const evidence = {
      id: "11111111-2222-4333-8444-555555555555",
      detectedAtMonotonicMilliseconds: 11_900,
      startProgress: .20,
      endProgress: .27,
      routeDistanceMeters: 70,
      worldDistanceMeters: 35,
      gainMeters: 35,
      maximumLateralOffsetMeters: 12,
      protectedRouteMeters: 55,
      theoreticalSavingMeters: 28,
      missedCriticalGates: 2,
      confidence: .93,
      flags: 1 | 2 | 4
    };
    const update = {
      ...telemetry(), clientMonotonicMilliseconds: 12_000, trackProgress: .27,
      lateralOffsetMeters: 0, trackLengthMeters: 1_000, shortcutEvidence: evidence
    };

    core.updateTelemetry(participantId, update, new Date(started.getTime() + 1_000));
    expect(core.snapshot().participants[0].penalties.filter(item => item.kind === "time")).toHaveLength(1);
    expect(core.snapshot().participants[0].penalties[0].reason).toContain("弯道路程");

    core.updateTelemetry(participantId,
      { ...update, clientMonotonicMilliseconds: 12_100 },
      new Date(started.getTime() + 1_100));
    expect(core.snapshot().participants[0].penalties).toHaveLength(1);

    core.updateTelemetry(participantId, {
      ...update,
      clientMonotonicMilliseconds: 12_200,
      isOnPitRoute: true,
      shortcutEvidence: {
        ...evidence,
        id: "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
        detectedAtMonotonicMilliseconds: 12_150
      }
    }, new Date(started.getTime() + 1_200));
    expect(core.snapshot().participants[0].penalties).toHaveLength(1);
  });

  it("keeps an automatic-yellow candidate across one invalid telemetry update", () => {
    const core = createCore();
    const participantId = connect(core, "甲");
    const started = new Date("2026-08-09T12:45:00Z");
    core.applySession({ phase: "qualifying", qualifyingMinutes: 10 }, started);
    const slow = { ...telemetry(), speedKph: 2 };
    core.updateTelemetry(participantId, slow, new Date(started.getTime() + 1_000));
    core.updateTelemetry(participantId, { ...slow, isTelemetryValid: false, isPausedOrRewinding: true },
      new Date(started.getTime() + 2_000));
    core.updateTelemetry(participantId, slow, new Date(started.getTime() + 4_200));
    expect(core.snapshot().flag).toBe("yellow");
    expect(core.snapshot().yellowZones.some(zone => zone.isAutomatic)).toBe(true);
  });

  it("requires configured teams and enforces each team capacity", () => {
    const core = createCore();
    const settings = core.roomSettings();
    expect(core.applyRoomSettings({ ...settings, allowTeams: true, teamCount: 2, driversPerTeam: 1,
      teams: [
        { id: "red", name: "红队", themeColor: "#FF4057" },
        { id: "blue", name: "蓝队", themeColor: "#5A8CFF" }
      ] }).ok).toBe(true);
    expect(core.login({ ...login("无车队"), teamId: null, teamName: null })).toMatchObject({ ok: false, code: "teamRequired" });
    expect(core.login({ ...login("甲"), teamId: "red", teamName: "红队" }).ok).toBe(true);
    expect(core.login({ ...login("乙"), teamId: "red", teamName: "红队" })).toMatchObject({ ok: false, code: "teamFull" });
    expect(core.login({ ...login("乙"), teamId: "blue", teamName: "蓝队" }).ok).toBe(true);
  });

  it("auto-assigns an available configured team to LazyForza 1.4.2 clients", () => {
    const core = createCore();
    const settings = core.roomSettings();
    expect(core.applyRoomSettings({ ...settings, allowTeams: true, teamCount: 2, driversPerTeam: 1,
      teams: [
        { id: "red", name: "红队", themeColor: "#FF4057" },
        { id: "blue", name: "蓝队", themeColor: "#5A8CFF" }
      ] }).ok).toBe(true);

    expect(core.login({ ...login("旧客户端甲"), clientVersion: "1.4.2", teamId: undefined, teamName: null }).ok).toBe(true);
    expect(core.login({ ...login("旧客户端乙"), clientVersion: "v1.4.2", teamId: undefined,
      teamName: "旧客户端自由填写的名称" }).ok).toBe(true);
    expect(core.snapshot().participants.map(participant => participant.teamId).sort()).toEqual(["blue", "red"]);
    expect(core.login({ ...login("新客户端"), clientVersion: "1.4.3", teamId: undefined, teamName: null }))
      .toMatchObject({ ok: false, code: "teamRequired" });
  });

  it("previews the chequered flag for the leader near the finish without ending the race", () => {
    const core = createCore();
    const leader = connect(core, "甲");
    core.applySession({ phase: "race", totalRaceLaps: 2 });
    core.completeLap(leader, lap("lap-1", 60, true, 1));
    core.updateTelemetry(leader, { ...telemetry(), trackProgress: .95 });
    const approaching = core.snapshot();
    expect(approaching.chequeredImminent).toBe(true);
    expect(approaching.flag).toBe("green");
    expect(approaching.participants[0].status).not.toBe("finished");
    core.completeLap(leader, lap("lap-2", 61, true, 2));
    expect(core.snapshot()).toMatchObject({ flag: "chequered", chequeredImminent: false });
  });

  it("enforces the configured minimum number of effective pit services at the finish", () => {
    const core = createCore();
    expect(core.applyRoomSettings({
      ...core.roomSettings(),
      totalRaceLaps: 1,
      minimumRequiredPitStops: 1
    }).ok).toBe(true);
    const withoutStop = connect(core, "未进站");
    const withStop = connect(core, "已进站");
    core.applySession({ phase: "race", totalRaceLaps: 1 });
    core.updateTelemetry(withStop, {
      ...telemetry(),
      isInPitLane: true,
      isInServiceZone: true,
      pitServiceRequirementMet: true,
      completedPitServices: 1
    });

    core.completeLap(withoutStop, lap("minimum-stop-missed", 60, true, 1));
    core.completeLap(withStop, lap("minimum-stop-complete", 61, true, 1));

    const snapshot = core.snapshot();
    expect(snapshot.minimumRequiredPitStops).toBe(1);
    expect(snapshot.participants.find(item => item.id === withoutStop)?.status).toBe("disqualified");
    expect(snapshot.participants.find(item => item.id === withStop)?.status).toBe("finished");
    expect(snapshot.penalties?.some(item => item.participantId === withoutStop &&
      item.kind === "disqualification" && item.isAutomatic)).toBe(true);
  });

  it("uses an acknowledged pit service event as the idempotent authority", () => {
    const core = createCore();
    const participantId = connect(core, "可靠换胎");
    const started = new Date("2026-08-23T10:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 3 }, started);
    const raceStartedAt = Date.parse(core.snapshot(started).startsAt!);
    const visitId = "pit-visit-1";
    core.updateTelemetry(participantId, {
      ...telemetry(),
      isInPitLane: true,
      isInServiceZone: true,
      pitServiceElapsedSeconds: 3,
      pitServiceRequirementMet: true,
      completedPitServices: 1,
      pitServiceVisitId: visitId
    }, new Date(started.getTime() + 1_000));
    expect(core.snapshot().participants[0].completedPitServices).toBe(0);

    const completed = {
      eventId: "pit-event-1",
      visitId,
      completedPitServices: 1,
      requiredSeconds: 2.5,
      elapsedSeconds: 2.5,
      clientMonotonicMilliseconds: 20_000,
      raceStartedAtUnixMilliseconds: raceStartedAt
    };
    expect(core.completePitService(participantId, completed,
      new Date(started.getTime() + 3_000)).ok).toBe(true);
    expect(core.completePitService(participantId, completed,
      new Date(started.getTime() + 4_000)).ok).toBe(true);
    expect(core.completePitService(participantId, { ...completed, eventId: "pit-event-retry" },
      new Date(started.getTime() + 5_000)).ok).toBe(true);
    expect(core.snapshot().participants[0].completedPitServices).toBe(1);
  });

  it("accepts a pre-finish pit visit completed after the line and repairs the result", () => {
    const core = createCore();
    expect(core.applyRoomSettings({
      ...core.roomSettings(),
      totalRaceLaps: 1,
      minimumRequiredPitStops: 1
    }).ok).toBe(true);
    const participantId = connect(core, "跨线换胎");
    const started = new Date("2026-08-23T11:00:00Z");
    core.applySession({ phase: "race", totalRaceLaps: 1 }, started);
    const raceStartedAt = Date.parse(core.snapshot(started).startsAt!);
    core.updateTelemetry(participantId, {
      ...telemetry(),
      trackProgress: .99,
      isInPitLane: true,
      isInServiceZone: true,
      pitServiceElapsedSeconds: 2,
      pitServiceVisitId: "cross-line-visit"
    }, new Date(started.getTime() + 58_000));
    core.completeLap(participantId, lap("cross-line-finish", 60, true, 1),
      new Date(started.getTime() + 60_000));
    expect(core.snapshot().participants[0].status).toBe("disqualified");

    const recovered = core.completePitService(participantId, {
      eventId: "cross-line-service",
      visitId: "cross-line-visit",
      completedPitServices: 1,
      requiredSeconds: 2.5,
      elapsedSeconds: 2.7,
      clientMonotonicMilliseconds: 61_000,
      raceStartedAtUnixMilliseconds: raceStartedAt
    }, new Date(started.getTime() + 61_000));
    expect(recovered.ok).toBe(true);
    const corrected = core.snapshot();
    expect(corrected.participants[0].status).toBe("finished");
    expect(corrected.participants[0].completedPitServices).toBe(1);
    expect(corrected.penalties?.find(penalty => penalty.kind === "disqualification")?.isRevoked).toBe(true);
    expect(core.events().some(event => event.type === "pitServiceCompletedRecovered")).toBe(true);
    expect(core.events().some(event => event.type === "minimumPitStopsRecovered")).toBe(true);
    expect(core.results()[0].participants[0].status).toBe("finished");

    core.updateTelemetry(participantId, {
      ...telemetry(),
      trackProgress: .02,
      isInPitLane: true,
      isInServiceZone: true,
      pitServiceVisitId: "post-finish-visit"
    }, new Date(started.getTime() + 62_000));
    expect(core.completePitService(participantId, {
      eventId: "post-finish-service",
      visitId: "post-finish-visit",
      completedPitServices: 2,
      requiredSeconds: 2.5,
      elapsedSeconds: 2.5,
      clientMonotonicMilliseconds: 63_000,
      raceStartedAtUnixMilliseconds: raceStartedAt
    }, new Date(started.getTime() + 63_000)).ok).toBe(false);
    expect(core.snapshot().participants[0].completedPitServices).toBe(1);
  });
});

function createCore(maximumParticipants = 12): RaceCore {
  return new RaceCore({
    sessionName: "测试赛事",
    maximumParticipants,
    totalRaceLaps: 5,
    minimumRequiredPitStops: 0
  });
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
    teamName: "车队 1",
    teamId: "team-1",
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
