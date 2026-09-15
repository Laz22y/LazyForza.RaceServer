import { describe, expect, it } from "vitest";
import { RaceProgressTimeline } from "../src/race-progress-timeline";

describe("RaceProgressTimeline", () => {
  it("keeps delayed and duplicate receipts out of passage times", () => {
    const t = new RaceProgressTimeline();
    t.observe(.8, 0, 0, 48_000, 48); t.observe(.1, 0, 0, 70_000, 70); t.observe(.4, 0, 0, 90_000, 90);
    const before = structuredClone(t.samples);
    t.confirmCompletedLaps(1); t.confirmCompletedLaps(1);
    expect(t.samples).toEqual(before);
    expect(t.passageTime(1.4)).toBe(90);
    t.observe(.5, 1, 1, 95_000, 95);
    expect(t.passageTime(1.5)).toBe(95);
  });
  it("accepts next-lap telemetry past three quarters after its receipt anchor", () => {
    const t = new RaceProgressTimeline();
    t.observe(.8, 0, 0, 48_000, 48); t.confirmCompletedLaps(1);
    t.observe(.99, 0, 1, 59_000, 61);
    expect(t.samples.at(-1)!.distanceLaps).toBe(.8);
    t.observe(.85, 1, 1, 110_000, 110);
    expect(t.samples.at(-1)!.distanceLaps).toBeCloseTo(1.85, 5);
    expect(t.passageTime(1.85)).toBe(110);
  });
  it("cannot gain another lap of distance by backing over the finish", () => {
    const t = new RaceProgressTimeline();
    t.observe(.98, 0, 0, 58_000, 58); t.observe(.01, 0, 0, 60_000, 60);
    t.observe(.999, 0, 0, 61_000, 61); t.observe(.015, 0, 0, 62_000, 62);
    expect(t.samples.at(-1)!.distanceLaps).toBeCloseTo(1.015, 5);
    expect(t.passageTime(1.01)).toBe(60);
  });
  it("ignores older packets and retains the first passage during a stationary stop", () => {
    const t = new RaceProgressTimeline();
    t.observe(.98, 0, 0, 58_000, 58); t.observe(.02, 0, 0, 62_000, 62);
    t.observe(.8, 0, 0, 57_000, 63); t.observe(.02, 0, 0, 70_000, 70); t.observe(.1, 0, 0, 80_000, 80);
    expect(t.samples).toHaveLength(3);
    expect(t.passageTime(1.02)).toBe(62); expect(t.passageTime(1.1)).toBe(80);
  });
  it("resets the clock on reconnect and the entire timeline on a new stage", () => {
    const t = new RaceProgressTimeline();
    t.observe(.98, 0, 0, 58_000, 58); t.resetClientClock(); t.observe(.02, 0, 0, 500, 68);
    expect(t.samples.at(-1)!.distanceLaps).toBeCloseTo(1.02, 5);
    t.reset(); expect(t.samples).toHaveLength(0);
    t.observe(.98, 0, 0, 100, 1); t.observe(.01, 0, 0, 200, 3);
    expect(t.samples).toHaveLength(1); expect(t.samples.at(-1)!.distanceLaps).toBeCloseTo(.01, 5);
  });
  it("bounds long dense histories and interpolates forward passages", () => {
    const t = new RaceProgressTimeline();
    for (let i = 1; i <= 20_000; i++) t.observe(i % 1000 / 1000, 0, 0, i * 100, 10 + i / 10);
    expect(t.samples.length).toBeLessThanOrEqual(3600);
    expect(t.samples.at(-1)!.distanceLaps).toBeCloseTo(20, 5);
    expect(t.passageTime(19.9995)).toBeCloseTo(2009.95, 5);
    expect(t.passageTime(10)).toBeNull();
  });
});
