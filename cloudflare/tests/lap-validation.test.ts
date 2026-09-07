import sharedCases from "./fixtures/lap-validation-cases.json";
import { describe, expect, it } from "vitest";
import { validateLap, type LapValidationSample } from "../src/lap-validation";
import type { LapCompleted } from "../src/protocol";

interface Case { name: string; status: string; canAccept: boolean; samples?: string; number?: number;
  seconds?: number; sectors?: number[]; valid?: boolean; legacy?: boolean; wrongStage?: boolean; lastNumber?: number; lastValid?: boolean }
const cases: Case[] = sharedCases;
const stage = "11111111-1111-1111-1111-111111111111";
const base: LapCompleted = { eventId: "22222222-2222-2222-2222-222222222222", lapNumber: 1,
  lapSeconds: 60, sectorSeconds: [20,20,20], isValid: true, clientMonotonicMilliseconds: 60000, stageId: stage };
describe("shared lap validation contract", () => {
  it.each(cases)("$name", item => {
    const lap = { ...base, lapNumber: item.number ?? 1, lapSeconds: item.seconds ?? 60,
      sectorSeconds: item.sectors ?? [20,20,20], isValid: item.valid ?? true,
      stageId: item.legacy ? null : item.wrongStage ? "33333333-3333-3333-3333-333333333333" : stage };
    const samples: LapValidationSample[] = item.samples == null ? [] : Array.from({length: 61}, (_, i) => i)
      .filter(i => item.samples !== "gap" || i < 20 || i > 30)
      .map(i => ({ at: i * 1000, progress: item.samples === "stationary" ? .2 : i === 60 ? 0 : i / 60,
        reliable: item.samples !== "paused" || i !== 30, pit: item.samples === "pit" && i >= 20 && i <= 40 }));
    const result = validateLap(lap, stage, 3, item.lastNumber, item.lastValid ?? false, samples);
    expect(result.canAccept).toBe(item.canAccept);
    expect(result.status).toBe(item.status);
  });
  it("rejects nonfinite and unsafe numbers", () => {
    for (const bad of [{lapSeconds: NaN}, {lapSeconds: Infinity}, {sectorSeconds: [20,NaN,20]},
      {clientMonotonicMilliseconds: Number.MAX_SAFE_INTEGER + 1}, {lapNumber: 2147483648}, {eventId: ""}])
      expect(validateLap({...base, ...bad}, stage, 3, null, false, []).canAccept).toBe(false);
  });
});
