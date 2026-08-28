import { describe, expect, it } from "vitest";
import type { RoomSettings } from "../src/protocol";
import {
  createRuleTemplate,
  deleteRuleTemplate,
  normalizeRuleTemplates,
  roomSettingsFromRuleTemplate,
  updateRuleTemplate
} from "../src/rule-templates";

describe("race rule templates", () => {
  it("creates, normalizes, reloads, updates and deletes templates", () => {
    const created = createRuleTemplate([], {
      name: "  冲刺赛  ",
      rules: {
        totalRaceLaps: 1_200,
        minimumRequiredPitStops: -2,
        sectorCount: 0,
        countdownSeconds: 150,
        practiceSessionCount: 2,
        practiceSessionMinutes: [45, 240],
        qualifyingSessionCount: 3,
        qualifyingSessionMinutes: [18, 15, 0],
        qualifyingEliminationCounts: [5, 20]
      }
    }, new Date("2026-08-28T08:00:00Z"));

    expect(created.template).toMatchObject({ name: "冲刺赛", rules: {
      totalRaceLaps: 999,
      minimumRequiredPitStops: 0,
      sectorCount: 1,
      countdownSeconds: 120,
      practiceSessionMinutes: [45, 180],
      qualifyingSessionMinutes: [18, 15, 1],
      qualifyingEliminationCounts: [5, 11]
    }});
    const reloaded = normalizeRuleTemplates(JSON.parse(JSON.stringify(created.templates)));
    expect(reloaded).toHaveLength(1);
    expect(() => createRuleTemplate(reloaded, { name: "冲刺赛" })).toThrow("同名");

    const updated = updateRuleTemplate(reloaded, created.template.id, {
      name: "耐力赛", rules: { totalRaceLaps: 120, countdownSeconds: 30 }
    }, new Date("2026-08-28T09:00:00Z"));
    expect(updated.template.createdAt).toBe(created.template.createdAt);
    expect(updated.template.updatedAt).toBe("2026-08-28T09:00:00.000Z");
    expect(updated.template.rules.totalRaceLaps).toBe(120);
    const removed = deleteRuleTemplate(updated.templates, created.template.id);
    expect(removed.deleted).toBe(true);
    expect(removed.templates).toEqual([]);
  });

  it("applies rules while preserving event, track and team identity", () => {
    const current: RoomSettings = {
      sessionName: "周末正赛", totalRaceLaps: 10, sectorCount: 3,
      automaticYellowEnabled: true, automaticCollisionInvestigationsEnabled: false,
      disconnectedLapRecoveryEnabled: false, slowSpeedKph: 12, slowDurationSeconds: 3,
      severeLateralOffsetMeters: 25, recoveryDurationSeconds: 3,
      allowTeams: true, trackName: "山谷环线", trackId: "track-7",
      trackRevision: "rev-7", trackPackageHash: "HASH-7",
      teamCount: 2, driversPerTeam: 6, trackLimitMode: "warningsOnly",
      minimumRequiredPitStops: 1,
      teams: [
        { id: "factory", name: "厂队", themeColor: "#123456" },
        { id: "privateer", name: "私人车队", themeColor: "#ABCDEF" }
      ]
    };
    const created = createRuleTemplate([], { name: "耐力赛", rules: {
      totalRaceLaps: 120, minimumRequiredPitStops: 4, sectorCount: 5,
      automaticYellowEnabled: false, automaticCollisionInvestigationsEnabled: true,
      disconnectedLapRecoveryEnabled: true, trackLimitMode: "automatic",
      teamCount: 3, driversPerTeam: 4
    }});

    const merged = roomSettingsFromRuleTemplate(created.template, current);

    expect(merged).toMatchObject({
      sessionName: current.sessionName,
      trackName: current.trackName,
      trackId: current.trackId,
      trackRevision: current.trackRevision,
      trackPackageHash: current.trackPackageHash,
      totalRaceLaps: 120,
      minimumRequiredPitStops: 4,
      sectorCount: 5,
      automaticYellowEnabled: false,
      automaticCollisionInvestigationsEnabled: true,
      disconnectedLapRecoveryEnabled: true,
      trackLimitMode: "automatic",
      teamCount: 3,
      driversPerTeam: 4
    });
    expect(merged.teams?.slice(0, 2)).toEqual(current.teams);
    expect(merged.teams?.[2].name).toBe("车队 3");
  });
});
