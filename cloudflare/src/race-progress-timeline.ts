/** Physical passage times, independent of pit service, penalties and scored laps. */
export class RaceProgressTimeline {
  readonly samples: { distanceLaps: number; elapsedSeconds: number }[] = [];
  private lapOffset = 0;
  private initialized = false;
  private awaitingAnchor = false;
  private previousProgress: number | null = null;
  private lastClientMilliseconds = 0;

  reset(): void {
    this.samples.length = 0;
    this.lapOffset = 0;
    this.initialized = this.awaitingAnchor = false;
    this.previousProgress = null;
    this.resetClientClock();
  }

  // A reconnected process can have a new monotonic clock. Its race distance survives.
  resetClientClock(): void { this.lastClientMilliseconds = 0; }

  confirmCompletedLaps(completedLaps: number): void {
    if (this.initialized && this.lapOffset >= completedLaps) return;
    this.initialized = this.awaitingAnchor = true;
    this.lapOffset = completedLaps;
    // Receipt time is not passage time: the next ordered telemetry supplies it.
  }

  observe(progress: number, reportedLaps: number, scoredLaps: number,
    clientMilliseconds: number, elapsedSeconds: number): void {
    if (!Number.isFinite(progress) || !Number.isFinite(elapsedSeconds) || elapsedSeconds < 0) return;
    if (clientMilliseconds > 0 && this.lastClientMilliseconds > clientMilliseconds) return;
    this.lastClientMilliseconds = Math.max(this.lastClientMilliseconds, clientMilliseconds);
    progress = Math.max(0, Math.min(progress, 1));
    if (!this.initialized) {
      // The initial grid crossing starts lap zero, without scoring a lap.
      this.lapOffset = scoredLaps === 0 && elapsedSeconds < 10 && progress > .75 ? -1 : scoredLaps;
      this.initialized = true;
    }
    if (this.awaitingAnchor) {
      const crossed = this.previousProgress !== null && this.previousProgress - progress > .5;
      if (reportedLaps < this.lapOffset && !crossed && progress > .25) return;
      this.awaitingAnchor = false;
    } else if (this.previousProgress !== null) {
      if (this.previousProgress - progress > .5) this.lapOffset++;
      // Backing over the line and returning must reach the same distance again.
      else if (progress - this.previousProgress > .75) this.lapOffset--;
    }
    this.previousProgress = progress;
    const distance = this.lapOffset + progress;
    const last = this.samples.at(-1);
    if (distance < 0 || last && (distance <= last.distanceLaps || elapsedSeconds < last.elapsedSeconds)) return;
    // Keep the FIRST passage time, including during a stationary service stop.
    this.samples.push({ distanceLaps: distance, elapsedSeconds });
    let removeCount = 0;
    while (removeCount < this.samples.length - 2 && this.samples[removeCount].distanceLaps < distance - 1.25)
      removeCount++;
    removeCount = Math.max(removeCount, this.samples.length - 3_600);
    if (removeCount > 0) this.samples.splice(0, removeCount);
  }

  passageTime(distance: number): number | null {
    if (!this.samples.length || distance < this.samples[0].distanceLaps || distance > this.samples.at(-1)!.distanceLaps)
      return null;
    let lower = 0, upper = this.samples.length - 1;
    while (lower < upper) {
      const middle = lower + Math.floor((upper - lower) / 2);
      if (this.samples[middle].distanceLaps < distance) lower = middle + 1;
      else upper = middle;
    }
    const next = this.samples[lower];
    if (Math.abs(next.distanceLaps - distance) <= 1e-9) return next.elapsedSeconds;
    if (lower === 0) return null;
    const previous = this.samples[lower - 1];
    const fraction = (distance - previous.distanceLaps) / (next.distanceLaps - previous.distanceLaps);
    return previous.elapsedSeconds + (next.elapsedSeconds - previous.elapsedSeconds) * fraction;
  }
}
