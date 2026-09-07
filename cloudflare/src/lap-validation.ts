import type { LapCompleted, LapValidationStatus } from "./protocol";

export interface LapValidationSample { at: number; progress: number; reliable: boolean; pit: boolean }
export interface LapValidationResult { canAccept: boolean; status: LapValidationStatus; reason: string }

export function validateLap(lap: LapCompleted, stageId: string | null | undefined, sectorCount: number,
  lastNumber: number | null | undefined, lastWasValid: boolean, samples: LapValidationSample[]): LapValidationResult {
  const reject = (reason: string): LapValidationResult => ({ canAccept: false, status: "rejected", reason });
  const accept = (status: LapValidationStatus, reason: string): LapValidationResult => ({ canAccept: true, status, reason });
  if (typeof lap.eventId !== "string" || !lap.eventId || lap.eventId.toLowerCase() === "00000000-0000-0000-0000-000000000000" || !Number.isInteger(lap.lapNumber) || lap.lapNumber < 1 || lap.lapNumber > 2_147_483_647 ||
      !Number.isSafeInteger(lap.clientMonotonicMilliseconds) || lap.clientMonotonicMilliseconds < 0)
    return reject("圈事件编号、圈序或客户端时间无效。");
  if (lap.stageId != null && (typeof lap.stageId !== "string" || lap.stageId.toLowerCase() !== stageId?.toLowerCase()))
    return reject("圈事件不属于当前赛事阶段。");
  if (lastNumber != null && (lap.lapNumber < lastNumber || lap.lapNumber === lastNumber && lastWasValid))
    return reject("圈序已经处理或已过期。");
  if (!Number.isFinite(lap.lapSeconds) || lap.lapSeconds < (lap.isValid ? 3 : 0) || lap.lapSeconds > 21_600)
    return reject("圈速超出允许范围。");
  if (!Array.isArray(lap.sectorSeconds) || (lap.isValid || lap.sectorSeconds.length > 0) && lap.sectorSeconds.length !== sectorCount)
    return reject("圈事件分段数量与赛道不一致。");
  if (lap.sectorSeconds.some(value => !Number.isFinite(value) || value < 0 || value > 21_600))
    return reject("圈事件分段时间无效。");
  if (!lap.isValid) return accept("rejected", "客户端已将本圈标为无效。");
  if (lastNumber != null && lap.lapNumber > lastNumber + 1)
    return accept("pendingReview", "圈序存在缺口，需核对遗漏圈事件。");
  if (Math.abs(lap.sectorSeconds.reduce((sum, value) => sum + value, 0) - lap.lapSeconds) > Math.max(.05, lap.lapSeconds * .001))
    return accept("pendingReview", "分段总时与圈时不一致，需核对计时样本。");
  const window = samples.filter(item => item.at >= lap.clientMonotonicMilliseconds - lap.lapSeconds * 1000 && item.at <= lap.clientMonotonicMilliseconds);
  let sufficient = window.length >= 8 && window.every(item => item.reliable && !item.pit) &&
    window[0].at - (lap.clientMonotonicMilliseconds - lap.lapSeconds * 1000) <= 1500 &&
    lap.clientMonotonicMilliseconds - window[window.length - 1].at <= 1500;
  let progress = 0;
  for (let i = 1; i < window.length && sufficient; i++) {
    const gap = window[i].at - window[i - 1].at;
    let delta = window[i].progress - window[i - 1].progress;
    if (delta < -.5) delta += 1;
    if (gap <= 0 || gap > 2000 || delta < -.02 || delta > .25) sufficient = false;
    else progress += Math.max(0, delta);
  }
  if (sufficient && lap.stageId != null && lap.sectorSeconds.every(value => value > 0) && (progress < .65 || progress > 1.35))
    return accept("pendingReview", "连续遥测进度与完成一圈不一致，需人工核对。");
  if (!sufficient || lap.stageId == null || lap.sectorSeconds.some(value => value === 0))
    return accept("insufficientEvidence", "阶段或连续遥测证据不足，未作完整交叉核验。");
  return accept("verified", "圈事件结构与连续遥测交叉核验通过。");
}
