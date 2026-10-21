import { Injectable, signal } from '@angular/core';
import { local, readJson, writeJson } from './storage';

const KEY = 'th.layout';

export const DOCK_DEFAULT = 0.32;
export const DOCK_MIN = 0.12;
export const DOCK_MAX = 0.85;

export const clampDock = (fraction: number) =>
  Number.isFinite(fraction) ? Math.min(DOCK_MAX, Math.max(DOCK_MIN, fraction)) : DOCK_DEFAULT;

/** How much of the height (below the top bar) the ask dock takes. Remembered across visits. */
@Injectable({ providedIn: 'root' })
export class Layout {
  readonly dock = signal(clampDock(readJson<{ dock?: number }>(local(), KEY)?.dock ?? DOCK_DEFAULT));

  setDock(fraction: number) {
    this.dock.set(clampDock(fraction));
  }

  /** Called when a drag ends rather than on every pointer move. */
  save() {
    writeJson(local(), KEY, { dock: this.dock() });
  }
}
