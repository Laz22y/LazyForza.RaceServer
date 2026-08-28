import {
  clamp,
  clampInteger,
  cleanText,
  isThemeColor,
  type RoomSettings,
  type TeamDefinition,
  type TrackLimitMode
} from "./protocol";

export const maximumRuleTemplates = 32;

const fallbackTeamColors = [
  "#42D7E8", "#FF4057", "#5A8CFF", "#FFD328", "#B86CFF", "#34D17B",
  "#FF8A3D", "#EE4FA6", "#B8F34A", "#8FA3B8", "#6FD6A7", "#F28B82"
];

export interface RaceRuleTemplateRules {
  totalRaceLaps: number;
  minimumRequiredPitStops: number;
  sectorCount: number;
  automaticYellowEnabled: boolean;
  automaticCollisionInvestigationsEnabled: boolean;
  disconnectedLapRecoveryEnabled: boolean;
  slowSpeedKph: number;
  slowDurationSeconds: number;
  severeLateralOffsetMeters: number;
  recoveryDurationSeconds: number;
  trackLimitMode: TrackLimitMode;
  allowTeams: boolean;
  teamCount: number;
  driversPerTeam: number;
  countdownSeconds: number;
  practiceSessionCount: number;
  practiceSessionMinutes: number[];
  qualifyingSessionCount: number;
  qualifyingSessionMinutes: number[];
  qualifyingEliminationCounts: Array<number | null>;
}

export interface RaceRuleTemplateSnapshot {
  id: string;
  name: string;
  createdAt: string;
  updatedAt: string;
  rules: RaceRuleTemplateRules;
}

export interface RaceRuleTemplateSaveRequest {
  name?: unknown;
  rules?: Partial<RaceRuleTemplateRules> | null;
}

export function normalizeRuleTemplateRules(
  candidate?: Partial<RaceRuleTemplateRules> | null): RaceRuleTemplateRules {
  const source = candidate ?? {};
  const practiceSessionCount = clampInteger(source.practiceSessionCount ?? 1, 1, 3);
  const qualifyingSessionCount = clampInteger(source.qualifyingSessionCount ?? 1, 1, 3);
  return {
    totalRaceLaps: clampInteger(source.totalRaceLaps ?? 10, 1, 999),
    minimumRequiredPitStops: clampInteger(source.minimumRequiredPitStops ?? 1, 0, 20),
    sectorCount: clampInteger(source.sectorCount ?? 3, 1, 20),
    automaticYellowEnabled: source.automaticYellowEnabled !== false,
    automaticCollisionInvestigationsEnabled: source.automaticCollisionInvestigationsEnabled === true,
    disconnectedLapRecoveryEnabled: source.disconnectedLapRecoveryEnabled === true,
    slowSpeedKph: clamp(source.slowSpeedKph ?? 12, 3, 50),
    slowDurationSeconds: clamp(source.slowDurationSeconds ?? 3, 1, 15),
    severeLateralOffsetMeters: clamp(source.severeLateralOffsetMeters ?? 25, 5, 200),
    recoveryDurationSeconds: clamp(source.recoveryDurationSeconds ?? 3, 1, 15),
    trackLimitMode: source.trackLimitMode === "automatic" || source.trackLimitMode === "disabled"
      ? source.trackLimitMode : "warningsOnly",
    allowTeams: source.allowTeams !== false,
    teamCount: clampInteger(source.teamCount ?? 2, 1, 12),
    driversPerTeam: clampInteger(source.driversPerTeam ?? 6, 1, 12),
    countdownSeconds: clampInteger(source.countdownSeconds ?? 10, 0, 120),
    practiceSessionCount,
    practiceSessionMinutes: normalizeMinutes(
      source.practiceSessionMinutes,
      practiceSessionCount,
      [60, 60, 60]),
    qualifyingSessionCount,
    qualifyingSessionMinutes: normalizeMinutes(
      source.qualifyingSessionMinutes,
      qualifyingSessionCount,
      qualifyingSessionCount === 1 ? [10] : [18, 15, 12]),
    qualifyingEliminationCounts: Array.from(
      { length: Math.max(0, qualifyingSessionCount - 1) },
      (_, index) => {
        const value = source.qualifyingEliminationCounts?.[index];
        return value === null || value === undefined ? null : clampInteger(value, 0, 11);
      })
  };
}

export function normalizeRuleTemplates(source: unknown): RaceRuleTemplateSnapshot[] {
  if (!Array.isArray(source)) return [];
  const ids = new Set<string>(), names = new Set<string>(), result: RaceRuleTemplateSnapshot[] = [];
  for (const candidate of source.slice(-maximumRuleTemplates) as Array<Partial<RaceRuleTemplateSnapshot>>) {
    const id = cleanText(candidate?.id, 80), name = cleanText(candidate?.name, 64);
    const createdAt = cleanDate(candidate?.createdAt), updatedAt = cleanDate(candidate?.updatedAt);
    if (!id || !name || !createdAt || !updatedAt || ids.has(id) || names.has(name.toLowerCase())) continue;
    ids.add(id);names.add(name.toLowerCase());
    result.push({ id, name, createdAt, updatedAt, rules: normalizeRuleTemplateRules(candidate.rules) });
  }
  return result;
}

export function createRuleTemplate(
  source: RaceRuleTemplateSnapshot[],
  request: RaceRuleTemplateSaveRequest,
  now = new Date()): { templates: RaceRuleTemplateSnapshot[]; template: RaceRuleTemplateSnapshot } {
  const templates = normalizeRuleTemplates(source);
  if (templates.length >= maximumRuleTemplates)
    throw new Error(`规则模板最多保存 ${maximumRuleTemplates} 个。`);
  const name = normalizeName(request.name);
  if (!name) throw new Error("规则模板名称不能为空。");
  if (templates.some(item => item.name.toLowerCase() === name.toLowerCase()))
    throw new Error("已经存在同名规则模板。");
  const at = now.toISOString();
  const template = {
    id: crypto.randomUUID(), name, createdAt: at, updatedAt: at,
    rules: normalizeRuleTemplateRules(request.rules)
  };
  return { templates: [...templates, template], template };
}

export function updateRuleTemplate(
  source: RaceRuleTemplateSnapshot[],
  id: string,
  request: RaceRuleTemplateSaveRequest,
  now = new Date()): { templates: RaceRuleTemplateSnapshot[]; template: RaceRuleTemplateSnapshot } {
  const templates = normalizeRuleTemplates(source), index = templates.findIndex(item => item.id === id);
  if (index < 0) throw new RangeError("规则模板不存在。");
  const name = normalizeName(request.name);
  if (!name) throw new Error("规则模板名称不能为空。");
  if (templates.some(item => item.id !== id && item.name.toLowerCase() === name.toLowerCase()))
    throw new Error("已经存在同名规则模板。");
  const template = {
    ...templates[index], name, updatedAt: now.toISOString(), rules: normalizeRuleTemplateRules(request.rules)
  };
  templates[index] = template;
  return { templates, template };
}

export function deleteRuleTemplate(
  source: RaceRuleTemplateSnapshot[],
  id: string): { templates: RaceRuleTemplateSnapshot[]; deleted: boolean } {
  const templates = normalizeRuleTemplates(source), retained = templates.filter(item => item.id !== id);
  return { templates: retained, deleted: retained.length !== templates.length };
}

export function roomSettingsFromRuleTemplate(
  template: RaceRuleTemplateSnapshot,
  current: RoomSettings): RoomSettings {
  const rules = normalizeRuleTemplateRules(template.rules);
  return {
    ...current,
    totalRaceLaps: rules.totalRaceLaps,
    minimumRequiredPitStops: rules.minimumRequiredPitStops,
    sectorCount: rules.sectorCount,
    automaticYellowEnabled: rules.automaticYellowEnabled,
    automaticCollisionInvestigationsEnabled: rules.automaticCollisionInvestigationsEnabled,
    disconnectedLapRecoveryEnabled: rules.disconnectedLapRecoveryEnabled,
    slowSpeedKph: rules.slowSpeedKph,
    slowDurationSeconds: rules.slowDurationSeconds,
    severeLateralOffsetMeters: rules.severeLateralOffsetMeters,
    recoveryDurationSeconds: rules.recoveryDurationSeconds,
    trackLimitMode: rules.trackLimitMode,
    allowTeams: rules.allowTeams,
    teamCount: rules.teamCount,
    driversPerTeam: rules.driversPerTeam,
    teams: resizeTeams(current.teams, rules.teamCount)
  };
}

function normalizeMinutes(
  source: number[] | undefined,
  count: number,
  fallback: number[]): number[] {
  return Array.from({ length: count }, (_, index) =>
    clampInteger(source?.[index] ?? fallback[Math.min(index, fallback.length - 1)], 1, 180));
}

function resizeTeams(source: TeamDefinition[] | undefined, requestedCount: number): TeamDefinition[] {
  const count = clampInteger(requestedCount, 1, 12), current = Array.isArray(source) ? source : [];
  const ids = new Set<string>(), names = new Set<string>(), result: TeamDefinition[] = [];
  for (let index = 0; index < count; index++) {
    const existing = current[index];
    let id = cleanText(existing?.id, 40) ?? `team-${index + 1}`;
    while (ids.has(id.toLowerCase())) id += "-next";
    ids.add(id.toLowerCase());
    let name = cleanText(existing?.name, 24) ?? `车队 ${index + 1}`;
    while (names.has(name.toLowerCase())) name += "-";
    names.add(name.toLowerCase());
    result.push({
      id,
      name,
      themeColor: isThemeColor(existing?.themeColor)
        ? existing.themeColor.toUpperCase()
        : fallbackTeamColors[index % fallbackTeamColors.length]
    });
  }
  return result;
}

function normalizeName(value: unknown): string | null {
  return cleanText(value, 64);
}

function cleanDate(value: unknown): string | null {
  const text = cleanText(value, 80);
  return text && Number.isFinite(Date.parse(text)) ? new Date(text).toISOString() : null;
}
