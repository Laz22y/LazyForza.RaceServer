import { describe, expect, it } from "vitest";
import {
  activateEventProject,
  captureEventProject,
  copyEventProject,
  createEventProject,
  eventProjectContentDisposition,
  exportEventProjectPackage,
  importEventProjectPackage,
  syncActiveEventProject,
  type EventProjectContext,
  type EventProjectSaveRequest
} from "../src/event-projects";
import type { StageResultSnapshot } from "../src/protocol";

describe("event projects", () => {
  it("creates, activates, synchronizes and copies reusable projects", () => {
    const now = new Date("2026-08-28T10:00:00Z");
    const created = createEventProject([], request(" 周末杯 "), context(), now);

    expect(created.project.name).toBe("周末杯");
    expect(created.project.schedule.countdownSeconds).toBe(120);
    expect(created.project.schedule.practiceSessionMinutes).toEqual([45, 180]);

    const activated = activateEventProject(created.projects, created.project.id, new Date(now.getTime() + 1_000));
    const resultId = crypto.randomUUID();
    const synchronized = syncActiveEventProject(
      activated.projects,
      [result("2026-08-28T11:00:00Z", resultId)],
      [{ sequence: 8, occurredAt: "2026-08-28T10:30:00Z", type: "phaseChanged", message: "练习赛开始" }],
      new Date("2026-08-28T11:00:00Z"));
    expect(synchronized.changed).toBe(true);
    expect(synchronized.projects[0].results).toHaveLength(1);
    expect(synchronized.projects[0].auditEvents[0].occurredAt).toBe("2026-08-28T10:30:00Z");

    const revised = syncActiveEventProject(
      synchronized.projects,
      [result("2026-08-28T11:00:00Z", resultId, 60.5)],
      [{ sequence: 8, occurredAt: "2026-08-28T10:30:00Z", type: "phaseChanged", message: "练习赛开始" }],
      new Date("2026-08-28T11:01:00Z"));
    expect(revised.changed).toBe(true);
    expect(revised.projects[0].results[0].fastestLapSeconds).toBe(60.5);
    expect(revised.projects[0].revision).toBe(synchronized.projects[0].revision + 1);

    const unchanged = syncActiveEventProject(
      revised.projects,
      [result("2026-08-28T11:00:00Z", resultId, 60.5)],
      [{ sequence: 8, occurredAt: "2026-08-28T10:30:00Z", type: "phaseChanged", message: "练习赛开始" }],
      new Date("2026-08-28T11:02:00Z"));
    expect(unchanged.changed).toBe(false);
    expect(unchanged.projects[0].revision).toBe(revised.projects[0].revision);

    const copied = copyEventProject(revised.projects, created.project.id, "下一站", new Date("2026-08-28T12:00:00Z"));
    expect(copied.project.status).toBe("draft");
    expect(copied.project.results).toEqual([]);
    expect(copied.project.auditEvents).toEqual([]);
  });

  it("isolates results by event and retains rules while editing metadata", async () => {
    const a = createEventProject([],request("A"),context());
    const b = createEventProject(a.projects,request("B"),context());
    const active = activateEventProject(b.projects,a.project.id,new Date(),a.project.id);
    const own = {...result(new Date().toISOString()),eventId:a.project.id};
    const other = {...result(new Date().toISOString()),eventId:b.project.id};
    const updated = captureEventProject(active.projects,a.project.id,{...request("Renamed"),schedule:{countdownSeconds:7}},
      {...context(),room:{...context().room,totalRaceLaps:99},results:[own,other],events:[
        {sequence:1,occurredAt:new Date().toISOString(),type:"result",message:"A",eventId:a.project.id},
        {sequence:2,occurredAt:new Date().toISOString(),type:"result",message:"B",eventId:b.project.id}]});
    expect(updated.project.room.totalRaceLaps).toBe(20);
    expect(updated.project.schedule).toEqual(a.project.schedule);
    expect(updated.project.results.map(r=>r.id)).toEqual([own.id]);
    expect(updated.project.auditEvents.map(e=>e.message)).toEqual(["A"]);
    const next = activateEventProject(updated.projects,b.project.id,new Date(),b.project.id);
    expect(next.projects.find(p=>p.id===a.project.id)?.status).toBe("completed");
    expect(()=>activateEventProject(next.projects,a.project.id)).toThrow();
    const synced = syncActiveEventProject(next.projects,[own,other],[]);
    expect(synced.projects.find(p=>p.id===b.project.id)?.results.map(r=>r.id)).toEqual([other.id]);
    const exported = await exportEventProjectPackage(updated.project,{trackPackage:null,organizerLogo:null});
    expect((await importEventProjectPackage(exported)).project.eventId).toBe(a.project.id);
    const copy = copyEventProject(next.projects,a.project.id,"Next");
    expect(copy.project.eventId).toBeNull();
    expect(copy.project.results).toEqual([]);
  });

  it("round-trips packages and rejects modified payloads", async () => {
    const created = createEventProject([], request("耐力赛"), {
      ...context(),
      results: [result("2026-08-28T11:00:00Z")],
      events: [{ sequence: 3, occurredAt: "2026-08-28T11:01:00Z", type: "result", message: "成绩已固化" }]
    }, new Date("2026-08-28T10:00:00Z"));
    const bytes = await exportEventProjectPackage(created.project, { trackPackage: null, organizerLogo: null });
    const disposition = eventProjectContentDisposition(created.project);
    expect(disposition).toContain("filename*=UTF-8''%E8%80%90%E5%8A%9B%E8%B5%9B.lfzevent");
    expect(disposition).not.toMatch(/\p{Script=Han}/u);
    const imported = await importEventProjectPackage(bytes, new Set(), new Date("2026-08-28T12:00:00Z"));

    expect(imported.project.id).toBe(created.project.id);
    expect(imported.project.status).toBe("completed");
    expect(imported.project.results).toHaveLength(1);
    expect(imported.project.auditEvents).toHaveLength(1);

    const conflicting = await importEventProjectPackage(
      bytes, new Set([created.project.id]), new Date("2026-08-28T13:00:00Z"));
    expect(conflicting.project.id).not.toBe(created.project.id);

    const tampered = new Uint8Array(bytes.slice(0));
    const needle = new TextEncoder().encode("耐力赛");
    const index = find(tampered, needle);
    expect(index).toBeGreaterThan(0);
    tampered[index] ^= 1;
    await expect(importEventProjectPackage(tampered.buffer)).rejects.toThrow("校验失败");
  });
});

function request(name: string): EventProjectSaveRequest {
  return {
    name,
    shortName: "WC",
    organizer: "LazyForza Club",
    description: "周末测试赛事",
    scheduledStartAt: "2026-09-01T08:00:00+08:00",
    timeZoneId: "Asia/Shanghai",
    schedule: {
      countdownSeconds: 130,
      practiceSessionCount: 2,
      practiceSessionMinutes: [45, 240],
      qualifyingSessionCount: 3,
      qualifyingSessionMinutes: [18, 15, 12],
      qualifyingEliminationCounts: [3, 2]
    }
  };
}

function context(): EventProjectContext {
  return {
    room: {
      sessionName: "周末杯",
      totalRaceLaps: 20,
      sectorCount: 3,
      automaticYellowEnabled: true,
      automaticCollisionInvestigationsEnabled: true,
      disconnectedLapRecoveryEnabled: true,
      slowSpeedKph: 12,
      slowDurationSeconds: 3,
      severeLateralOffsetMeters: 25,
      recoveryDurationSeconds: 3,
      allowTeams: true,
      trackName: "山谷环线",
      trackId: null,
      trackRevision: null,
      trackPackageHash: null,
      teamCount: 1,
      driversPerTeam: 6,
      teams: [{ id: "team-1", name: "厂队", themeColor: "#42D7E8" }],
      trackLimitMode: "warningsOnly",
      minimumRequiredPitStops: 1
    },
    results: [],
    events: [],
    trackPackage: null,
    organizerLogo: null
  };
}

function result(completedAt: string, id = crypto.randomUUID(), fastestLapSeconds = 61.25): StageResultSnapshot {
  return {
    id,
    phase: "practice",
    label: "FP1",
    sessionNumber: 1,
    sessionCount: 2,
    isComplete: true,
    completedAt,
    sessionName: "周末杯",
    trackName: "山谷环线",
    fastestParticipantId: "4d56df89-bba9-48ec-8c83-c2a1c8719b08",
    fastestLapSeconds,
    participants: [{
      id: "4d56df89-bba9-48ec-8c83-c2a1c8719b08",
      position: 1,
      displayName: "Driver 1",
      themeColor: "#42D7E8",
      teamName: "厂队",
      teamColor: "#42D7E8",
      status: "onTrack",
      completedLaps: 5,
      trackProgress: 0.8,
      bestLapSeconds: fastestLapSeconds,
      raceTotalSeconds: null,
      adjustedRaceTotalSeconds: null,
      gapToLeaderSeconds: null,
      timePenaltySeconds: 0,
      penalties: []
    }]
  };
}

function find(source: Uint8Array, needle: Uint8Array): number {
  outer: for (let index = 0; index <= source.length - needle.length; index++) {
    for (let offset = 0; offset < needle.length; offset++)
      if (source[index + offset] !== needle[offset]) continue outer;
    return index;
  }
  return -1;
}
